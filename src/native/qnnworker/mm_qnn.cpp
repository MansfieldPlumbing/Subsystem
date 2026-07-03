// Sovereign on-device QNN MatMul receipt — raw QnnGraph API, no python, no converter.
// Builds y = x @ W on a chosen backend, finalizes (HTP -> on-device prepare), emits the
// context .bin via contextGetBinary, executes, and diffs against an in-program CPU reference.
// Ships in the APK as libqnnworker.so (exec'd child rung; see Dpx/DpxQnnWorker.cs — the primary
// rung is in-process via the <uses-native-library> public-vendor-library door in the manifest).
//   usage: ./mm_qnn <backend.so> [out.bin] [soc_id] [dsp_arch]
//   e.g.   ./mm_qnn libQnnCpu.so                    (validates the graph path, no NPU)
//          ./mm_qnn libQnnHtp.so mm_v73.bin         (HTP, stock null-config deviceCreate)
//          ./mm_qnn libQnnHtp.so mm_v73.bin 43 73   (HTP, explicit SoC: SM8550=43 / SM8635=68,
//                                                    kokoro htp_ext precedent — soc_id + dsp_arch)
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cstdint>
#include <cmath>
#include <cstdarg>
#include <vector>
#include <dlfcn.h>
#include <unistd.h>

#include "QnnInterface.h"
#include "HTP/QnnHtpGraph.h"
#include "HTP/QnnHtpDevice.h"

static void logcb(const char* fmt, QnnLog_Level_t level, uint64_t ts, va_list ap) {
  (void)level; (void)ts; vfprintf(stderr, fmt, ap); fprintf(stderr, "\n");
}

#define CK(call) do { Qnn_ErrorHandle_t _e=(call); \
  if(_e!=QNN_SUCCESS){ fprintf(stderr,"[FAIL] %s -> 0x%llx (line %d)\n",#call,(unsigned long long)_e,__LINE__); return 2;} } while(0)

int main(int argc, char** argv) {
  const char* backendLib = argc>1 ? argv[1] : "libQnnHtp.so";
  const char* binOut     = argc>2 ? argv[2] : nullptr;
  uint32_t socModel      = argc>3 ? (uint32_t)atoi(argv[3]) : 0;   // 0 = stock null-config
  uint32_t dspArch       = argc>4 ? (uint32_t)atoi(argv[4]) : 73;
  bool isHtp = strstr(backendLib,"Htp") || strstr(backendLib,"htp");

  void* h = dlopen(backendLib, RTLD_NOW|RTLD_LOCAL);
  if(!h){ fprintf(stderr,"dlopen %s: %s\n", backendLib, dlerror()); return 2; }
  typedef Qnn_ErrorHandle_t (*GetProviders_t)(const QnnInterface_t***, uint32_t*);
  auto getProviders = (GetProviders_t)dlsym(h,"QnnInterface_getProviders");
  if(!getProviders){ fprintf(stderr,"no QnnInterface_getProviders in %s\n",backendLib); return 2; }
  const QnnInterface_t** providers=nullptr; uint32_t np=0;
  CK(getProviders(&providers,&np));
  if(np<1||!providers){ fprintf(stderr,"no providers\n"); return 2; }
  QNN_INTERFACE_VER_TYPE qnn = providers[0]->QNN_INTERFACE_VER_NAME;
  fprintf(stderr,"[ok] backend loaded: %s (providers=%u)\n", backendLib, np);

  Qnn_LogHandle_t log=nullptr;
  if(qnn.logCreate) qnn.logCreate(logcb, QNN_LOG_LEVEL_ERROR, &log);
  Qnn_BackendHandle_t backend=nullptr;
  CK(qnn.backendCreate(log, nullptr, &backend));

  // deviceCreate: stock QnnSampleApp shape (single call, UNSUPPORTED_FEATURE=1000 tolerated,
  // proceed with whatever handle came back). soc_id != 0 threads an explicit QnnHtpDevice
  // custom config (SOC + minimum ARCH) instead of the backend's auto-detect — the kokoro
  // htp_ext_kokoro.json precedent (soc_id 43 / dsp_arch v73) expressed through QnnDevice.
  Qnn_DeviceHandle_t device=nullptr;
  if(qnn.deviceCreate){
    Qnn_ErrorHandle_t drc;
    if(isHtp && socModel){
      QnnHtpDevice_CustomConfig_t cSoc; memset(&cSoc,0,sizeof(cSoc));
      cSoc.option = QNN_HTP_DEVICE_CONFIG_OPTION_SOC;
      cSoc.socModel = socModel;
      QnnHtpDevice_CustomConfig_t cArch; memset(&cArch,0,sizeof(cArch));
      cArch.option = QNN_HTP_DEVICE_CONFIG_OPTION_ARCH;
      cArch.arch.deviceId = 0;
      cArch.arch.arch = (QnnHtpDevice_Arch_t)dspArch;
      QnnDevice_Config_t dSoc; memset(&dSoc,0,sizeof(dSoc));
      dSoc.option = QNN_DEVICE_CONFIG_OPTION_CUSTOM; dSoc.customConfig = &cSoc;
      QnnDevice_Config_t dArch; memset(&dArch,0,sizeof(dArch));
      dArch.option = QNN_DEVICE_CONFIG_OPTION_CUSTOM; dArch.customConfig = &cArch;
      const QnnDevice_Config_t* dcs[]={&dSoc,&dArch,nullptr};
      drc = qnn.deviceCreate(log, dcs, &device);
      fprintf(stderr,"[ok] deviceCreate soc=%u arch=v%u -> 0x%llx\n", socModel, dspArch, (unsigned long long)drc);
    } else {
      drc = qnn.deviceCreate(log, nullptr, &device);
      fprintf(stderr,"[ok] deviceCreate null-config -> 0x%llx%s\n", (unsigned long long)drc,
              drc==1000?" (UNSUPPORTED_FEATURE, stock-ok)":"");
    }
  }
  Qnn_ContextHandle_t ctx=nullptr;
  CK(qnn.contextCreate(backend, device, nullptr, &ctx));
  fprintf(stderr,"[ok] context created\n");

  Qnn_GraphHandle_t graph=nullptr;
  if(isHtp){
    QnnHtpGraph_CustomConfig_t prec; memset(&prec,0,sizeof(prec));
    prec.option = QNN_HTP_GRAPH_CONFIG_OPTION_PRECISION;
    prec.precision = QNN_PRECISION_FLOAT16;          // run fp16 on the HMX
    QnnGraph_Config_t gc; memset(&gc,0,sizeof(gc));
    gc.option = QNN_GRAPH_CONFIG_OPTION_CUSTOM;
    gc.customConfig = (QnnGraph_CustomConfig_t)&prec;
    const QnnGraph_Config_t* gcs[]={&gc,nullptr};
    CK(qnn.graphCreate(ctx,"matmul",gcs,&graph));
  } else {
    CK(qnn.graphCreate(ctx,"matmul",nullptr,&graph));
  }
  fprintf(stderr,"[ok] graph created (fp16=%d)\n", (int)isHtp);

  const uint32_t K=256, N=256;
  std::vector<float> W((size_t)K*N), x(K), yref(N,0.f), yout(N,0.f);
  uint64_t s=88172645463325252ull;
  auto rnd=[&](){ s^=s<<13; s^=s>>7; s^=s<<17; return ((double)(s%2000000)/1000000.0)-1.0; };
  for(auto&v:W) v=(float)(rnd()*0.05);
  for(auto&v:x) v=(float)rnd();
  for(uint32_t n=0;n<N;n++){ double a=0; for(uint32_t k=0;k<K;k++) a+=(double)x[k]*W[(size_t)k*N+n]; yref[n]=(float)a; }

  uint32_t dx[2]={1,K}, dw[2]={K,N}, dy[2]={1,N};
  auto mkT=[&](const char* name, Qnn_TensorType_t tt, uint32_t* dims, void* data, uint32_t bytes)->Qnn_Tensor_t{
    Qnn_Tensor_t t; t.version=QNN_TENSOR_VERSION_1; t.v1=QNN_TENSOR_V1_INIT;
    t.v1.name=name; t.v1.type=tt; t.v1.dataFormat=QNN_TENSOR_DATA_FORMAT_FLAT_BUFFER;
    t.v1.dataType=QNN_DATATYPE_FLOAT_32; t.v1.rank=2; t.v1.dimensions=dims;
    t.v1.memType=QNN_TENSORMEMTYPE_RAW; t.v1.clientBuf.data=data; t.v1.clientBuf.dataSize=bytes;
    return t;
  };
  Qnn_Tensor_t tx=mkT("x",QNN_TENSOR_TYPE_APP_WRITE,dx,nullptr,0);
  Qnn_Tensor_t tw=mkT("W",QNN_TENSOR_TYPE_STATIC,   dw,W.data(),(uint32_t)(W.size()*4));
  Qnn_Tensor_t ty=mkT("y",QNN_TENSOR_TYPE_APP_READ, dy,nullptr,0);
  CK(qnn.tensorCreateGraphTensor(graph,&tx));
  CK(qnn.tensorCreateGraphTensor(graph,&tw));
  CK(qnn.tensorCreateGraphTensor(graph,&ty));
  fprintf(stderr,"[ok] tensors created (x.id=%u W.id=%u y.id=%u)\n", tx.v1.id,tw.v1.id,ty.v1.id);

  Qnn_Tensor_t ins[2]={tx,tw}; Qnn_Tensor_t outs[1]={ty};
  Qnn_OpConfig_t op; op.version=QNN_OPCONFIG_VERSION_1; op.v1=QNN_OPCONFIG_V1_INIT;
  op.v1.name="mm0"; op.v1.packageName="qti.aisw"; op.v1.typeName="MatMul";
  op.v1.numOfInputs=2; op.v1.inputTensors=ins;
  op.v1.numOfOutputs=1; op.v1.outputTensors=outs;
  CK(qnn.graphAddNode(graph, op));
  fprintf(stderr,"[ok] MatMul node added\n");

  CK(qnn.graphFinalize(graph, nullptr, nullptr));
  fprintf(stderr,"[ok] graph finalized (HTP prepare on-device)\n");

  if(binOut){
    Qnn_ContextBinarySize_t bsz=0;
    if(qnn.contextGetBinarySize(ctx,&bsz)==QNN_SUCCESS && bsz>0){
      std::vector<uint8_t> buf(bsz); Qnn_ContextBinarySize_t wrote=0;
      if(qnn.contextGetBinary(ctx,buf.data(),bsz,&wrote)==QNN_SUCCESS){
        FILE* f=fopen(binOut,"wb"); if(f){ fwrite(buf.data(),1,(size_t)wrote,f); fclose(f);
          fprintf(stdout,"context binary: %llu bytes -> %s\n",(unsigned long long)wrote,binOut); }
      }
    }
  }

  tx.v1.clientBuf.data=x.data();    tx.v1.clientBuf.dataSize=K*4;
  ty.v1.clientBuf.data=yout.data(); ty.v1.clientBuf.dataSize=N*4;
  Qnn_Tensor_t ein[1]={tx}; Qnn_Tensor_t eout[1]={ty};
  CK(qnn.graphExecute(graph, ein, 1, eout, 1, nullptr, nullptr));
  fprintf(stderr,"[ok] graph executed\n");

  double maxabs=0, refmax=0;
  for(uint32_t n=0;n<N;n++){ double d=fabs((double)yout[n]-yref[n]); if(d>maxabs)maxabs=d; if(fabs(yref[n])>refmax)refmax=fabs(yref[n]); }
  double maxrel = maxabs/(refmax>1e-9?refmax:1.0);
  fprintf(stdout,"backend=%s soc=%u arch=v%u K=%u N=%u\n", backendLib,socModel,dspArch,K,N);
  fprintf(stdout,"y_qnn[0..3]= %.4f %.4f %.4f %.4f\n", yout[0],yout[1],yout[2],yout[3]);
  fprintf(stdout,"y_ref[0..3]= %.4f %.4f %.4f %.4f\n", yref[0],yref[1],yref[2],yref[3]);
  fprintf(stdout,"max|abs|=%.5f  max|rel|=%.5f  refmax=%.4f\n", maxabs, maxrel, refmax);
  fprintf(stdout,"VERDICT: %s\n", maxrel<2e-2 ? "PASS" : "FAIL");
  fflush(stdout); fflush(stderr);
  _exit(maxrel<2e-2 ? 0 : 1);   // bypass QNN fastrpc teardown abort-at-exit
}
