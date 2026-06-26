// onnx-surgeon.cpp — native, python-free ONNX inspector / surgeon for QNN-HTP prep.
//
// ONNX is just protobuf, so we read/write it directly with libprotobuf — no python,
// no onnxruntime, no torch. This is the "exit hatch": the graph surgery the HTP
// converter needs (kill unsupported ops, freeze dynamic shapes, STFT->Conv) done
// against the raw ModelProto.
//
//   onnx-surgeon inspect <model.onnx>              report ops, IO shapes, HTP-risky nodes
//   onnx-surgeon stft-decompose <in> <out>         replace STFT ops with Conv/Reshape/Transpose
//
// Build: build-surgeon.ps1
#include "onnx.pb.h"
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <fstream>
#include <iostream>
#include <map>
#include <set>
#include <stdexcept>
#include <string>
#include <vector>

using namespace onnx;

static const std::set<std::string> SPECTRAL = {
    "STFT", "DFT", "RFFT", "IRFFT", "FFT", "MelWeightMatrix"};

// Ops with no (or fragile) Hexagon HTP coverage — the surgery targets.
static const std::set<std::string> RISKY = {
    "STFT", "DFT", "RFFT", "IRFFT", "FFT", "MelWeightMatrix",
    "ScatterND", "GridSample", "NonZero", "Loop", "If", "Scan",
    "SequenceEmpty", "SplitToSequence", "ConcatFromSequence",
    "RandomUniformLike", "RandomNormalLike"};

static std::string dimStr(const TensorShapeProto& shp) {
    std::string s = "[";
    for (int i = 0; i < shp.dim_size(); ++i) {
        if (i) s += ", ";
        const auto& d = shp.dim(i);
        if (d.has_dim_value())      s += std::to_string(d.dim_value());
        else if (d.has_dim_param()) s += d.dim_param();          // dynamic axis
        else                        s += "?";
    }
    return s + "]";
}

static void io(const char* tag, const google::protobuf::RepeatedPtrField<ValueInfoProto>& v) {
    std::cout << "-- " << tag << " --\n";
    for (const auto& vi : v) {
        std::string d = "(no shape)";
        if (vi.type().has_tensor_type() && vi.type().tensor_type().has_shape())
            d = dimStr(vi.type().tensor_type().shape());
        std::cout << "  " << vi.name() << "  " << d << "\n";
    }
}

static void attrVal(const AttributeProto& a) {
    using A = AttributeProto;
    std::cout << "     attr " << a.name() << " = ";
    switch (a.type()) {
        case A::INT:    std::cout << a.i(); break;
        case A::FLOAT:  std::cout << a.f(); break;
        case A::STRING: std::cout << a.s(); break;
        case A::INTS:   for (int i = 0; i < a.ints_size(); ++i)   std::cout << (i ? "," : "") << a.ints(i);   break;
        case A::FLOATS: for (int i = 0; i < a.floats_size(); ++i) std::cout << (i ? "," : "") << a.floats(i); break;
        default:        std::cout << "<attr-type " << static_cast<int>(a.type()) << ">";
    }
    std::cout << "\n";
}

static int inspect(const std::string& path) {
    ModelProto m;
    std::ifstream in(path, std::ios::binary);
    if (!in) { std::cerr << "cannot open " << path << "\n"; return 1; }
    if (!m.ParseFromIstream(&in)) { std::cerr << "FATAL: not a valid ONNX protobuf\n"; return 1; }

    const auto& g = m.graph();
    std::cout << "=== " << path << " ===\n";
    std::cout << "opset:";
    for (const auto& op : m.opset_import())
        std::cout << " " << (op.domain().empty() ? "ai.onnx" : op.domain()) << "=" << op.version();
    std::cout << "\nnodes: " << g.node_size() << "   initializers: " << g.initializer_size() << "\n\n";

    io("inputs", g.input());
    io("outputs", g.output());

    std::map<std::string, int> hist;
    for (const auto& n : g.node()) hist[n.op_type()]++;
    std::vector<std::pair<std::string, int>> hv(hist.begin(), hist.end());
    std::sort(hv.begin(), hv.end(), [](auto& a, auto& b) { return a.second > b.second; });

    std::cout << "\n-- op histogram (" << hist.size() << " distinct) --\n";
    for (auto& [op, c] : hv) {
        std::cout << "  " << op << "  " << c;
        if (RISKY.count(op)) std::cout << "   <-- HTP-RISKY";
        std::cout << "\n";
    }

    std::cout << "\n-- spectral ops (HTP cannot delegate) --\n";
    int sp = 0;
    for (const auto& n : g.node())
        if (SPECTRAL.count(n.op_type())) {
            ++sp;
            std::cout << "  " << n.op_type() << "  name=" << n.name() << "\n";
            std::cout << "     inputs :"; for (const auto& i : n.input())  std::cout << " " << i; std::cout << "\n";
            std::cout << "     outputs:"; for (const auto& o : n.output()) std::cout << " " << o; std::cout << "\n";
            for (const auto& a : n.attribute()) attrVal(a);
        }
    if (!sp) std::cout << "  (none)\n";

    int risky = 0;
    for (auto& [op, c] : hist) if (RISKY.count(op)) risky += c;
    std::cout << "\nVERDICT: " << risky << " HTP-risky node(s)."
              << (risky ? " Needs surgery before qairt-converter.\n" : " Graph looks HTP-clean.\n");
    return 0;
}

// =============================================================================
// stft-decompose — replace each ONNX STFT op with an equivalent primitive subgraph
// HTP can delegate: a strided Conv1d against a precomputed cos/sin*window basis,
// reshaped to the ONNX STFT output layout [batch, frames, n_bins, 2].
//
// Math (matches chatterbox brutal_decomposed_stft, minus torch center-pad — the
// ONNX STFT op does NOT center-pad; framing starts at sample 0):
//   N = frame_length (n_fft),  hop = frame_step,  K = onesided ? N/2+1 : N
//   weight[k][0][n]   = cos(-2*pi*k*n/N) * window[n]    (real bank, channels 0..K-1)
//   weight[K+k][0][n] = sin(-2*pi*k*n/N) * window[n]    (imag bank, channels K..2K-1)
//   Conv(signal[B,1,L], weight[2K,1,N], stride=hop) -> [B, 2K, frames]
//   Reshape -> [B, 2, K, frames]   Transpose(perm 0,3,2,1) -> [B, frames, K, 2]
// =============================================================================

// Resolve a tensor by name from initializers, then from Constant-node `value` attrs.
static const TensorProto* resolveTensor(const GraphProto& g, const std::string& name) {
    for (const auto& init : g.initializer())
        if (init.name() == name) return &init;
    for (const auto& n : g.node())
        if (n.op_type() == "Constant" && n.output_size() > 0 && n.output(0) == name)
            for (const auto& a : n.attribute())
                if (a.name() == "value" && a.has_t()) return &a.t();
    return nullptr;
}

static int64_t tensorScalarInt(const TensorProto& t) {
    switch (t.data_type()) {
        case TensorProto::INT64:
            if (t.int64_data_size() > 0) return t.int64_data(0);
            if (t.has_raw_data() && t.raw_data().size() >= 8) { int64_t v; std::memcpy(&v, t.raw_data().data(), 8); return v; }
            break;
        case TensorProto::INT32:
            if (t.int32_data_size() > 0) return t.int32_data(0);
            if (t.has_raw_data() && t.raw_data().size() >= 4) { int32_t v; std::memcpy(&v, t.raw_data().data(), 4); return (int64_t)v; }
            break;
    }
    throw std::runtime_error("expected an int32/int64 scalar tensor");
}

static std::vector<float> tensorFloatVec(const TensorProto& t) {
    std::vector<float> out;
    if (t.data_type() == TensorProto::FLOAT) {
        if (t.float_data_size() > 0) { out.assign(t.float_data().begin(), t.float_data().end()); return out; }
        if (t.has_raw_data()) { size_t n = t.raw_data().size() / 4; out.resize(n); std::memcpy(out.data(), t.raw_data().data(), n * 4); return out; }
    } else if (t.data_type() == TensorProto::DOUBLE) {
        if (t.double_data_size() > 0) { for (double d : t.double_data()) out.push_back((float)d); return out; }
        if (t.has_raw_data()) { size_t n = t.raw_data().size() / 8; const char* p = t.raw_data().data();
            for (size_t i = 0; i < n; ++i) { double d; std::memcpy(&d, p + i * 8, 8); out.push_back((float)d); } return out; }
    }
    throw std::runtime_error("expected a float/double window tensor");
}

static void addFloatInit(GraphProto* g, const std::string& name, const std::vector<int64_t>& dims, const std::vector<float>& data) {
    auto* t = g->add_initializer();
    t->set_name(name);
    t->set_data_type(TensorProto::FLOAT);
    for (auto d : dims) t->add_dims(d);
    t->set_raw_data(data.data(), data.size() * sizeof(float));
}

static void addInt64Init(GraphProto* g, const std::string& name, const std::vector<int64_t>& data) {
    auto* t = g->add_initializer();
    t->set_name(name);
    t->set_data_type(TensorProto::INT64);
    t->add_dims((int64_t)data.size());
    t->set_raw_data(data.data(), data.size() * sizeof(int64_t));
}

static void attrInts(NodeProto* n, const char* name, const std::vector<int64_t>& v) {
    auto* a = n->add_attribute(); a->set_name(name); a->set_type(AttributeProto::INTS);
    for (auto x : v) a->add_ints(x);
}
static void attrInt(NodeProto* n, const char* name, int64_t v) {
    auto* a = n->add_attribute(); a->set_name(name); a->set_type(AttributeProto::INT); a->set_i(v);
}

static int stftDecompose(const std::string& inPath, const std::string& outPath) {
    ModelProto m;
    { std::ifstream in(inPath, std::ios::binary);
      if (!in) { std::cerr << "cannot open " << inPath << "\n"; return 1; }
      if (!m.ParseFromIstream(&in)) { std::cerr << "FATAL: not a valid ONNX protobuf\n"; return 1; } }
    auto* g = m.mutable_graph();

    std::vector<int> stftIdx;
    for (int i = 0; i < g->node_size(); ++i)
        if (g->node(i).op_type() == "STFT") stftIdx.push_back(i);
    if (stftIdx.empty()) {
        std::cout << "no STFT nodes found — writing unchanged copy\n";
        std::ofstream out(outPath, std::ios::binary);
        return m.SerializeToOstream(&out) ? 0 : 1;
    }

    std::map<int, std::vector<NodeProto>> repl;
    int counter = 0;
    const double TWO_PI = 6.283185307179586476925286766559;

    for (int idx : stftIdx) {
        const NodeProto& s = g->node(idx);
        if (s.input_size() < 2) { std::cerr << "STFT " << s.name() << " has <2 inputs\n"; return 1; }
        std::string sig    = s.input(0);
        std::string fsName = s.input(1);
        std::string winName = (s.input_size() > 2 && !s.input(2).empty()) ? s.input(2) : "";
        std::string flName  = (s.input_size() > 3 && !s.input(3).empty()) ? s.input(3) : "";

        int64_t onesided = 1;
        for (const auto& a : s.attribute()) if (a.name() == "onesided") onesided = a.i();

        const TensorProto* fsT = resolveTensor(*g, fsName);
        if (!fsT) { std::cerr << "STFT " << s.name() << ": frame_step is not a constant (" << fsName << ")\n"; return 1; }
        int64_t hop = tensorScalarInt(*fsT);

        int64_t N = 0;
        if (!flName.empty()) {
            const TensorProto* flT = resolveTensor(*g, flName);
            if (!flT) { std::cerr << "STFT " << s.name() << ": frame_length is not a constant\n"; return 1; }
            N = tensorScalarInt(*flT);
        }
        std::vector<float> window;
        if (!winName.empty()) {
            const TensorProto* wT = resolveTensor(*g, winName);
            if (!wT) { std::cerr << "STFT " << s.name() << ": window is not a constant\n"; return 1; }
            window = tensorFloatVec(*wT);
            if (N == 0) N = (int64_t)window.size();
        }
        if (N <= 0) { std::cerr << "STFT " << s.name() << ": cannot determine n_fft\n"; return 1; }
        if (window.empty()) window.assign((size_t)N, 1.0f);
        if ((int64_t)window.size() > N) { std::cerr << "STFT " << s.name() << ": window longer than n_fft\n"; return 1; }
        if ((int64_t)window.size() < N) {          // center-pad a short window to n_fft
            int64_t padL = (N - (int64_t)window.size()) / 2;
            std::vector<float> w2((size_t)N, 0.0f);
            for (size_t i = 0; i < window.size(); ++i) w2[(size_t)padL + i] = window[i];
            window.swap(w2);
        }

        int64_t K = onesided ? (N / 2 + 1) : N;
        std::vector<float> W((size_t)(2 * K) * (size_t)N);
        for (int64_t k = 0; k < K; ++k)
            for (int64_t n = 0; n < N; ++n) {
                double arg = -TWO_PI * (double)k * (double)n / (double)N;
                float win = window[(size_t)n];
                W[(size_t)k * N + n]           = (float)std::cos(arg) * win;   // real bank
                W[(size_t)(K + k) * N + n]     = (float)std::sin(arg) * win;   // imag bank (sin(-x) gives -sin -> ONNX imag sign)
            }

        std::string base = "stftd" + std::to_string(counter++);
        std::string Wname = base + "_w", s1 = base + "_s1", s2 = base + "_s2";
        std::string sig3 = base + "_sig3", conv = base + "_conv", r4 = base + "_r4";
        std::string outName = s.output(0);

        addFloatInit(g, Wname, {2 * K, 1, N}, W);
        addInt64Init(g, s1, {0, 1, -1});
        addInt64Init(g, s2, {0, 2, K, -1});

        std::vector<NodeProto> ns;
        { NodeProto n; n.set_op_type("Reshape"); n.set_name(base + "_reshape_sig");
          n.add_input(sig); n.add_input(s1); n.add_output(sig3); ns.push_back(n); }
        { NodeProto n; n.set_op_type("Conv"); n.set_name(base + "_conv");
          n.add_input(sig3); n.add_input(Wname); n.add_output(conv);
          attrInts(&n, "strides", {hop}); attrInts(&n, "pads", {0, 0});
          attrInts(&n, "dilations", {1}); attrInts(&n, "kernel_shape", {N}); attrInt(&n, "group", 1);
          ns.push_back(n); }
        { NodeProto n; n.set_op_type("Reshape"); n.set_name(base + "_reshape_split");
          n.add_input(conv); n.add_input(s2); n.add_output(r4); ns.push_back(n); }
        { NodeProto n; n.set_op_type("Transpose"); n.set_name(base + "_transpose");
          n.add_input(r4); n.add_output(outName); attrInts(&n, "perm", {0, 3, 2, 1}); ns.push_back(n); }
        repl[idx] = ns;

        std::cout << "STFT " << s.name() << ": n_fft=" << N << " hop=" << hop
                  << " onesided=" << onesided << " K=" << K << " win_len=" << window.size()
                  << "  ->  Conv[" << 2 * K << ",1," << N << "] + Reshape + Transpose\n";
    }

    // Rebuild the node list, swapping each STFT for its 4-node replacement in place
    // (keeps the graph topologically sorted).
    google::protobuf::RepeatedPtrField<NodeProto> oldNodes;
    oldNodes.Swap(g->mutable_node());
    for (int i = 0; i < oldNodes.size(); ++i) {
        auto it = repl.find(i);
        if (it != repl.end()) { for (auto& nn : it->second) g->add_node()->CopyFrom(nn); }
        else                  { g->add_node()->CopyFrom(oldNodes.Get(i)); }
    }

    std::ofstream out(outPath, std::ios::binary);
    if (!out) { std::cerr << "cannot write " << outPath << "\n"; return 1; }
    if (!m.SerializeToOstream(&out)) { std::cerr << "serialize failed\n"; return 1; }
    std::cout << "wrote " << outPath << "  (" << stftIdx.size() << " STFT node(s) decomposed)\n";
    return 0;
}

// dump — print a tensor (initializer or Constant-node value) by name: dtype, dims, values.
static int dumpTensor(const std::string& path, const std::string& name) {
    ModelProto m;
    { std::ifstream in(path, std::ios::binary);
      if (!in) { std::cerr << "cannot open " << path << "\n"; return 1; }
      if (!m.ParseFromIstream(&in)) { std::cerr << "FATAL: not a valid ONNX protobuf\n"; return 1; } }
    const TensorProto* t = resolveTensor(m.graph(), name);
    if (!t) { std::cerr << "tensor not found (initializer or Constant): " << name << "\n"; return 1; }
    std::cout << name << "  dtype=" << t->data_type() << "  dims=[";
    int64_t count = 1;
    for (int i = 0; i < t->dims_size(); ++i) { std::cout << (i ? "," : "") << t->dims(i); count *= t->dims(i); }
    std::cout << "]\n";
    int dt = t->data_type();
    int64_t show = count < 64 ? count : 64;
    if (dt == TensorProto::FLOAT || dt == TensorProto::DOUBLE) {
        auto v = tensorFloatVec(*t);
        for (size_t i = 0; i < v.size() && (int64_t)i < show; ++i) std::cout << (i ? ", " : "  ") << v[i];
        std::cout << (v.size() > (size_t)show ? " ...\n" : "\n");
    } else if (dt == TensorProto::INT64 || dt == TensorProto::INT32) {
        if (count == 1) { std::cout << "  " << tensorScalarInt(*t) << "\n"; }
        else if (t->int64_data_size() > 0) { for (int i = 0; i < t->int64_data_size() && i < show; ++i) std::cout << (i ? ", " : "  ") << t->int64_data(i); std::cout << "\n"; }
        else if (t->int32_data_size() > 0) { for (int i = 0; i < t->int32_data_size() && i < show; ++i) std::cout << (i ? ", " : "  ") << t->int32_data(i); std::cout << "\n"; }
        else if (t->has_raw_data()) { const char* p = t->raw_data().data(); for (int64_t i = 0; i < show; ++i) { int64_t x = 0; std::memcpy(&x, p + i * (dt == TensorProto::INT64 ? 8 : 4), dt == TensorProto::INT64 ? 8 : 4); std::cout << (i ? ", " : "  ") << x; } std::cout << "\n"; }
    } else { std::cout << "  (unhandled dtype for value print)\n"; }
    return 0;
}

int main(int argc, char** argv) {
    GOOGLE_PROTOBUF_VERIFY_VERSION;
    if (argc < 3) {
        std::cerr << "onnx-surgeon (native, python-free)\n"
                  << "Usage:\n"
                  << "  onnx-surgeon inspect <model.onnx>\n"
                  << "  onnx-surgeon stft-decompose <in.onnx> <out.onnx>\n"
                  << "  onnx-surgeon dump <model.onnx> <tensor-name>\n";
        return 1;
    }
    std::string cmd = argv[1];
    if (cmd == "inspect") return inspect(argv[2]);
    if (cmd == "stft-decompose") {
        if (argc < 4) { std::cerr << "usage: onnx-surgeon stft-decompose <in.onnx> <out.onnx>\n"; return 1; }
        try { return stftDecompose(argv[2], argv[3]); }
        catch (const std::exception& e) { std::cerr << "ERROR: " << e.what() << "\n"; return 1; }
    }
    if (cmd == "dump") {
        if (argc < 4) { std::cerr << "usage: onnx-surgeon dump <model.onnx> <tensor-name>\n"; return 1; }
        try { return dumpTensor(argv[2], argv[3]); }
        catch (const std::exception& e) { std::cerr << "ERROR: " << e.what() << "\n"; return 1; }
    }
    std::cerr << "unknown command: " << cmd << "\n";
    return 1;
}
