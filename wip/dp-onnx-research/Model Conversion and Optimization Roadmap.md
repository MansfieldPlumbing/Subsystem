# **Engineering Monolithic AI Runtimes: Architectural Graph Surgery for Static Single-Graph and LiteRT Model Deployments**

## **Introduction to Monolithic Graph Execution and the Orchestration Bottleneck**

The operationalization of modern deep learning architectures increasingly exposes a critical friction point between high-level development environments and low-level hardware execution engines. During the prototyping and initial deployment phases, complex machine learning applications—such as dual-path audio separation networks and autoregressive text-to-speech generators—are typically orchestrated using host-side interpreted languages. Python scripts routinely manage the sequential execution of independent model sub-components, handle intermediate memory allocations, and dictate control flow mechanisms like generation loops and conditional branching. While this decoupled, multi-stage paradigm allows for rapid iteration and debugging, it introduces severe latency bottlenecks and hardware underutilization when transitioned to production environments.  
The core of this inefficiency lies in the synchronization overhead between the host CPU and the hardware accelerator. Every time an execution loop returns control to the Python interpreter to calculate the next sequence step, the pipeline incurs boundary-crossing penalties, Global Interpreter Lock (GIL) contention, and non-fused memory transport overheads. Furthermore, the fragmentation of the computational graph prevents advanced compiler toolchains, such as NVIDIA's TensorRT and Google's LiteRT, from applying whole-graph optimizations. When a model is severed into discrete subgraphs, the compiler cannot perform cross-boundary kernel fusion, continuous memory planning, or constant folding, resulting in highly sub-optimal VRAM bandwidth utilization.  
Addressing these fundamental deployment constraints requires a paradigm shift toward monolithic, static, chunked single-graph topologies. By internalizing external host-side logic—including feature extraction, autoregressive loops, and mathematical domain transformations—directly into a unified intermediate representation (such as the Open Neural Network Exchange format), the entirety of the execution pipeline is handed over to the runtime compiler. This report provides an exhaustive, highly technical analysis of this unification process. Leveraging the architectural blueprint established by the Demucs\_v4\_TRT repository, the research systematically translates these principles to Resemble AI’s Chatterbox Turbo, detailing the exact mechanisms required to fuse its fragmented components into a static, single-graph ONNX model. The analysis subsequently extends into the mobile and edge compute frontier, outlining the necessary mathematical surgeries and toolchain pathways to package both Chatterbox Turbo and Demucs v4 as optimized, single-file LiteRT LLM (.litertlm) execution artifacts.

## **The Architectural Blueprint: Deconstructing Demucs\_v4\_TRT**

To comprehend the mechanics of single-graph unification, one must first analyze the highly successful implementation demonstrated within the MansfieldPlumbing/Demucs\_v4\_TRT repository. Hybrid Transformer Demucs (HTDemucs) v4 is an industry-standard source separation model characterized by a complex dual-path architecture. The model simultaneously processes audio signals through a time-domain branch and a frequency-domain branch. These parallel computational paths periodically cross-communicate through a central transformer bottleneck, allowing the network to leverage both phase-accurate temporal structures and high-resolution spectral features.

### **The Graph Severing Anti-Pattern and PyTorch Issue \#135343**

The primary challenge in deploying HTDemucs natively on accelerated inference engines is the Short-Time Fourier Transform (STFT) and its inverse counterpart (ISTFT). Historically, dynamic complex-number operations and specialized signal processing functions have suffered from inconsistent support across intermediate representation schemas and backend execution providers. Consequently, the standard industry workaround involves severing the computational graph. Developers routinely calculate the STFT on the host CPU using libraries like Librosa or NumPy, passing the resulting spectrogram as a secondary, independent input tensor alongside the raw waveform to the neural network.  
In the specific context of the HTDemucs architecture, this graph-severing approach introduces catastrophic systemic failures. Because the time and frequency branches cross-communicate at deep layers within the network, externalizing the STFT mathematically decouples the synchronized initialization of these branches. As documented extensively in PyTorch issue \#135343, feeding an externalized spectrogram into the separated model produces numerically incorrect outputs, rendering the source separation ineffective. The architectural integrity of HTDemucs strictly mandates that the spectral transformation occurs as an unbroken computational sequence derived directly from the input waveform within the same execution context.

### **The WaveformOnlyWrapper Internalization Strategy**

The Demucs\_v4\_TRT implementation elegantly circumvents this limitation by forcing the STFT computation back into the traced graph through a specialized structural encapsulation. By utilizing a custom PyTorch nn.Module wrapper, the developer ensures that the TensorRT compiler receives a singular, uninterrupted directed acyclic graph. The wrapper is fundamentally simple but architecturally profound, accepting a single unified input—the raw audio waveform—and dynamically invoking the model's internal \_spec() method to generate the spectral features within the bounds of the graph trace.  
The success of this graph internalization relies heavily on carefully orchestrated export constraints. Modern tracing compilers, particularly the TorchDynamo engine introduced in PyTorch 2.0, struggle to trace complex internal methods like \_spec() in dual-path structures without dropping operations or inserting unwanted graph breaks. Therefore, the export process must explicitly disable Dynamo (dynamo=False), falling back to the legacy PyTorch JIT tracer to map the operations correctly. Simultaneously, the ONNX opset version is forced to opset\_version=17. This versioning is not an arbitrary preference; Opset 17 represents the foundational floor for native complex Fast Fourier Transform (FFT) and STFT operator support within the ONNX schema, allowing the graph to legitimately represent the spectral transformations without custom operator fallbacks.  
Furthermore, the implementation utilizes constant folding during the export phase (do\_constant\_folding=True). This step pre-computes any static operations within the graph, simplifying the network topology before it reaches the TensorRT optimizer. While secondary optimization tools like NVIDIA's Polygraphy can perform similar graph cleanups post-export, handling the constant folding natively during the PyTorch-to-ONNX translation minimizes the risk of topological errors and ensures a cleaner ingestion phase for the TensorRT parser.

### **TensorRT Compilation and Inference Economics**

Once the monolithic ONNX graph is successfully exported, it is subjected to the TensorRT compilation phase via the build\_engine.py script. Because the graph contains the complete dual-path structure, the TensorRT optimizer can execute its full suite of kernel fusion algorithms. Independent convolutional layers across the time and frequency branches that share inputs are fused into wider, single-dispatch kernels. VRAM allocations for intermediate tensors that were previously required to pass data between the severed host and device are entirely eliminated.  
The benchmarking metrics resulting from this single-graph compilation are highly indicative of the efficiency gains. Evaluated on an NVIDIA RTX 3090 (Ampere architecture, sm86) using the TensorRT 10.15.1 SDK with FP16 precision, the execution profile demonstrates extraordinary throughput.

| Inference Metric | Measured Value (RTX 3090, TRT 10.15.1, FP16) |
| :---- | :---- |
| GPU Compute (Median per chunk) | 115.8 ms |
| GPU Compute (Mean per chunk) | 118.7 ms |
| GPU Compute (90th Percentile) | 129.6 ms |
| Host Latency (Mean) | 120.2 ms |
| Host-to-Device (H2D) Transfer | \~0.23 ms |
| Device-to-Host (D2H) Transfer | \~1.26 ms |
| Total Inference Throughput | 8.3 chunks/second |
| Engine Weights Footprint | 157 MiB |
| Execution Context VRAM | 403 MiB |
| Total VRAM Allocation | \~560 MiB |

*Table 1: Profiling metrics for the monolithic Demucs\_v4\_TRT execution engine, processing static chunks of dimension \`\`, corresponding to approximately 7.8 seconds of stereo audio.*  
For a standard three-minute commercial audio track, the input is windowed into approximately 23 sequential chunks. At an average of 119 milliseconds per chunk, the total pure GPU compute time is roughly 2.7 seconds. When accounting for host-side normalization, overlap-add accumulation to prevent boundary artifacts, and file I/O operations, the end-to-end processing time resolves to approximately 5 seconds. This represents a massive acceleration over native PyTorch execution, strictly enabled by the monolithic graph topology and aggressive kernel fusion.

## **Topographical Analysis of Chatterbox Turbo**

To apply the monolithic principles of the Demucs\_v4\_TRT repository to Resemble AI’s Chatterbox Turbo, one must first deconstruct the current state of the model's inference pipeline. Chatterbox Turbo is an advanced, ultrafast Text-to-Speech system engineered on a streamlined 350-million parameter architecture. It is designed explicitly for low-latency zero-shot voice agents, featuring a highly distilled audio diffusion decoder that reduces the typical waveform generation process from ten iterative steps down to a single step. Furthermore, it natively supports paralinguistic control tags (e.g., \[laugh\], \[sigh\]) and incorporates an imperceptible neural watermarking system known as PerTh.  
However, the official ONNX export for Chatterbox Turbo provided to the community is highly fragmented. The computational pipeline is divided into four entirely discrete ONNX subgraphs, stitched together by an orchestrating Python host script.

1. **Speech Encoder (speech\_encoder.onnx)**: This subgraph ingests the reference audio waveform used for zero-shot voice cloning. It processes the raw audio to extract dense speaker features, conditioning embeddings, and the initial acoustic prompt tokens required to guide the synthesis.  
2. **Embedding Module (embed\_tokens.onnx)**: A distinct graph that accepts integer input IDs derived from the input text and projects them into the high-dimensional semantic embedding space utilized by the transformer.  
3. **Autoregressive Language Model (language\_model.onnx)**: The core cognitive engine of the system. This Llama-derived transformer backbone iteratively predicts the sequence of acoustic tokens. It requires the management of complex dynamic inputs, including the current text embeddings, an evolving attention\_mask, position identifiers (position\_ids), and an extensive array of 16 individual Key-Value (KV) cache tensors corresponding to the multi-head attention mechanism (NUM\_KV\_HEADS=16, HEAD\_DIM=64).  
4. **Conditional Decoder (conditional\_decoder.onnx)**: The final subgraph that translates the sequence of predicted acoustic tokens back into high-fidelity continuous waveforms at a native 24,000 Hz sample rate, utilizing the speaker embeddings extracted in the first stage.

### **The Catastrophic Host-Side Autoregressive Bottleneck**

The fragmentation of the Chatterbox Turbo pipeline necessitates a Python for loop to drive the autoregressive token generation. In the standard implementation, the Python script initializes a trange loop bounded by a max\_new\_tokens integer. During every single iteration, the script invokes the language\_model\_session.run() method. It retrieves the output logits, routes them through a custom RepetitionPenaltyLogitsProcessor instantiated in NumPy, executes an argmax operation to determine the next token, dynamically concatenates the new token to the sequence, dynamically expands the attention\_mask, increments the position\_ids, and shuttles the newly computed present\_key\_values back into the inputs as the past\_key\_values for the subsequent iteration.  
This architecture represents the absolute worst-case scenario for hardware acceleration. The inference engine is trapped in an I/O bottleneck. For a single second of generated audio, the pipeline might require hundreds of iterations. Each iteration forces a context switch from the highly optimized C++ ONNX Runtime backend back to the Python interpreter. The host CPU must serialize the tensor arrays, pass them through the pybind11 boundary, execute the NumPy logits math under the constraints of the GIL, and push the massive KV cache tensors back across the PCIe bus to the GPU.  
The consequences of this fragmentation extend beyond mere latency; they severely impact execution stability. As documented in community issue trackers (such as Issue \#6 regarding onnxruntime-node), executing this fragmented model via WebAssembly or Node.js bindings frequently results in fatal memory corruption. When multi-threading is enabled (numThreads \> 1), the continuous, rapid-fire reallocation of dynamic KV cache tensors during the autoregressive loop triggers malloc heap corruption, causing the runtime to crash with exit code 134 after only 12 to 15 iterations. Consolidating this fragmented pipeline into a static, single-graph topology is not merely an exercise in latency optimization; it is a fundamental requirement for stable, production-grade deployment across diverse runtimes.

## **Engineering the Single-Graph Chatterbox Turbo**

Transforming the segmented Chatterbox Turbo pipeline into a monolithic entity requires replicating the WaveformOnlyWrapper methodology, but with a significantly higher degree of complexity due to the autoregressive nature of the language model. The host-side Python for loop must be eliminated and replaced with native ONNX control flow operators, specifically the onnx::Loop node, allowing the entire token generation sequence to execute entirely within the device memory space.

### **Static Chunking and KV Cache Pre-allocation**

To ensure compatibility with TensorRT and to prevent the memory fragmentation issues observed in the Node.js runtime, the graph must employ static chunking. TensorRT's memory planner functions optimally when tensor shapes are known at compile time. While ONNX supports dynamic axes, utilizing them for continuously growing tensors (like the KV cache) forces the runtime to perform expensive dynamic memory reallocations during inference.  
Instead of allowing the past\_key\_values tensors to grow iteratively, the graph must allocate a static memory block representing the maximum possible sequence length (MAX\_TOKENS). For example, the cache tensor shape is rigidly defined as \`\`. During the ONNX Loop execution, the graph utilizes an integer loop counter to perform in-place slice updates, injecting the present\_key\_values into the statically allocated cache matrix. Consequently, the attention\_mask is also pre-allocated as a static tensor, utilizing a causal triangular mask combined with a dynamic slice index to restrict attention strictly to the computed tokens, effectively blinding the model to the unpopulated, pre-allocated sections of the cache.

### **Internalizing the Logits Processor**

The NumPy-based RepetitionPenaltyLogitsProcessor must be mathematically translated into a directed acyclic subgraph of native ONNX operators. The penalty logic alters the probability distribution to discourage the model from generating repetitive acoustic stutters.  
In Python, the logic is expressed as:

Python  
score \= np.take\_along\_axis(scores, input\_ids, axis=1)  
score \= np.where(score \< 0, score \* penalty, score / penalty)  
np.put\_along\_axis(scores\_processed, input\_ids, score, axis=1)

To fuse this into the ONNX graph, the exporter must map these operations to their ONNX equivalents. np.take\_along\_axis maps to the ONNX GatherElements operator. The conditional modification is represented by a Less operator generating a boolean mask, which is fed into a Where operator. The Where node acts as a multiplexer, routing the tensor through either a Mul (multiplication) or Div (division) node based on the boolean mask. Finally, np.put\_along\_axis is realized via the ScatterElements operator, mutating the logits tensor before it is passed to the final ArgMax operator to extract the predicted token ID.

### **Graph Unification via TorchScript and ONNX Compose**

There are two primary pathways to construct the final monolithic ONNX graph: upstream PyTorch scripting or downstream ONNX graph surgery.  
Because torch.onnx.export utilizes a trace-based mechanism, it fundamentally cannot capture dynamic Python for loops. A standard trace will simply unroll the loop based on the specific max\_new\_tokens variable provided during the tracing execution, creating a massive, linear graph with thousands of duplicated layers. To preserve the actual cyclic structure, the PyTorch wrapper must be compiled using TorchScript (@torch.jit.script).  
The developer constructs a master PyTorch nn.Module that instantiates the speech encoder, the embedding module, the language model, and the decoder. The forward pass is decorated with @torch.jit.script, forcing the compiler to perform lexical analysis on the for loop and translate it into the onnx::Loop operator. The termination condition of the loop is defined by an Equal operator, continuously comparing the output of the ArgMax node against the designated STOP\_SPEECH\_TOKEN (6562). If the condition is met, the loop terminates early, and the gathered token array is routed directly to the Conditional Decoder subgraph for waveform synthesis.  
If PyTorch's JIT compiler fails to lower the heavily optimized attention mechanisms of the Llama backbone, an alternative downstream approach utilizes onnx.compose.merge\_models and onnx\_graphsurgeon. The developer exports the subgraphs individually, imports them into the GraphSurgeon intermediate representation, and manually constructs the Loop node.

Python  
import onnx\_graphsurgeon as gs  
import onnx

\# Load individual graphs  
encoder\_graph \= gs.import\_onnx(onnx.load("speech\_encoder.onnx"))  
llm\_graph \= gs.import\_onnx(onnx.load("language\_model.onnx"))

\#... Manual construction of the Loop node...  
\# Define loop body graph, map loop-carried dependencies (KV Cache)  
loop\_node \= gs.Node(op="Loop", inputs=\[trip\_count, cond,...\], outputs=\[...\])  
loop\_node.attrs\["body"\] \= loop\_body\_graph

\# Stitch pipeline together and export

By surgically appending the outputs of the text embedding node to the inputs of the newly constructed Loop node, and subsequently routing the loop's output tensor to the conditional decoder, the fragmented pipeline is forged into a single .onnx artifact.

### **TensorRT Compilation of the Monolithic Chatterbox**

Upon successful generation of the single-graph ONNX file, the deployment transitions to the TensorRT compilation phase. Using the trtexec binary or the PyTorch TensorRT backend, the unified model is compiled into an .engine file.  
Because the execution bounds are defined by static chunking (MAX\_TOKENS), the TensorRT builder can perform exhaustive memory footprint planning during the optimization phase. The entire generation loop executes on the GPU. The host CPU merely dispatches the initial text string and reference audio, and idles until the final 24,000 Hz waveform array is copied back across the PCIe bus. This architectural surgery completely eradicates the GIL contention and the iterative memory fragmentation that crippled the Node.js runtimes, achieving latencies highly suitable for real-time conversational agents.

## **The Edge Frontier: LiteRT and the.litertlm Ecosystem**

While TensorRT provides unparalleled inference acceleration for desktop and data-center environments equipped with NVIDIA silicon, the proliferation of AI across mobile devices, embedded IoT systems, and browser-based WebAssembly/WebGPU runtimes demands a more ubiquitous deployment infrastructure. Google’s LiteRT (formerly TensorFlow Lite) represents the industry standard for cross-platform edge execution, providing deep hardware acceleration across Qualcomm Hexagon NPUs, Apple Neural Engines, and diverse mobile GPUs.  
The recent evolution of this ecosystem has introduced the LiteRT LLM format (.litertlm). Recognizing that modern generative AI applications are rarely monolithic single-tensor models, but rather complex pipelines involving tokenizers, multi-modal feature extractors, and generation parameters, the .litertlm format acts as a unified container. It bundles the optimized flatbuffer execution graphs (.tflite files) alongside SentencePiece tokenizers, external weight files, and strict TOML-defined metadata, allowing the entire inference system to be distributed as a single, self-contained payload.

## **Packaging Chatterbox Turbo as a Single-File LiteRT LLM**

To port the newly unified Chatterbox Turbo architecture to edge devices, the monolithic PyTorch graph must be translated into the TensorFlow Lite format. This translation is brokered by the ai\_edge\_torch library, a direct compilation pathway from PyTorch to the TFLite runtime, built atop the TorchDynamo export mechanism.

### **AI Edge Torch Conversion**

The developer initializes the unified ChatterboxSingleGraphWrapper PyTorch module. Rather than exporting to ONNX, the model is passed to the ai\_edge\_torch.convert() API. The developer must provide a tuple of highly specific, statically shaped sample inputs to guide the Dynamo tracer.

Python  
import ai\_edge\_torch  
import torch

\# Initialize the unified monolithic model  
model \= ChatterboxSingleGraphWrapper()  
model.eval()

\# Define strict static shapes for chunked execution  
sample\_input\_ids \= torch.zeros((1, 256), dtype=torch.int64)  
sample\_audio\_values \= torch.randn(1, 1, 24000)

\# Convert directly to TFLite flatbuffer  
edge\_model \= ai\_edge\_torch.convert(model, (sample\_input\_ids, sample\_audio\_values))  
edge\_model.export("chatterbox\_turbo\_fused.tflite")

The conversion process systematically maps the PyTorch Core ATen operators to their corresponding TFLite dialects. The static chunking implemented previously is critical here; TFLite's control flow operators (tf.while\_loop equivalents) require rigid shape invariants for the loop variables to prevent catastrophic memory expansion during inference on constrained mobile devices.

### **Constructing the LiteRT-LM Container**

With the .tflite flatbuffer generated, the pipeline is packaged using the litert-lm-builder utility. This command-line tool parses a TOML configuration file to construct the final .litertlm archive. The TOML file acts as the architectural manifest, mapping the specific input and output tensors of the flatbuffer to the semantic expectations of the LiteRT runtime engine.

Ini, TOML  
\# chatterbox\_manifest.toml  
\[system\_metadata\]  
entries \=

\[\[section\]\]  
section\_type \= "TfLiteModel"  
data\_path \= "chatterbox\_turbo\_fused.tflite"

\[\[section\]\]  
section\_type \= "Tokenizer"  
data\_path \= "tokenizer.json"

\[model.start\_tokens\]  
model\_input\_name \= "input\_ids"

\[model.audio\_input\]  
model\_input\_name \= "audio\_values"

\[model.output\_logits\]  
model\_output\_name \= "synthesized\_waveform"

Executing litert-lm-builder toml \--path chatterbox\_manifest.toml output \--path chatterbox\_turbo.litertlm seals the artifact. This single file can now be deployed natively in Android applications using Kotlin bindings, iOS applications via Swift, or cross-platform web applications utilizing WebGPU. Because the artifact contains the audio feature extraction, the tokenization metadata, the language model loop, and the final waveform decoding within a single boundary, edge developers are entirely abstracted from the complexities of audio signal processing or KV cache management.

## **Advanced Deployment: Demucs v4 as an Optimized LiteRT LLM**

While packaging Chatterbox Turbo into a .litertlm relies heavily on standard transformer abstractions, applying the same edge deployment strategy to the Demucs\_v4\_TRT model presents an extreme technical challenge. The core obstacle stems from the fundamental capabilities of the target execution engines. While TensorRT introduced robust support for complex arithmetic and Open Neural Network Exchange Fourier operations in Opset 17, the TensorFlow Lite and LiteRT backends remain highly constrained in their digital signal processing capabilities.  
Native support for PyTorch's torch.stft and torch.istft operators does not exist within the standard TFLite builtin operator library. Attempting an ai\_edge\_torch.convert() pass on the WaveformOnlyWrapper will instantly trigger fatal lowering exceptions during the ATen dialect translation phase, as the compiler has no corresponding TFLite operation to map the complex spectral transformations to.

### **Mathematical Surgery: Transmuting Fourier Transforms to Convolutions**

To achieve single-file edge deployment, the mathematical transformations must be surgically refactored. The graph must be rewritten using standard neural network primitives that are natively accelerated by mobile NPUs and GPUs. This is accomplished by replacing the Short-Time Fourier Transform with 1D Convolutions (nn.Conv1d), and the Inverse Short-Time Fourier Transform with 1D Transposed Convolutions (nn.ConvTranspose1d).  
The STFT computes the projection of a time-domain signal onto a set of complex sinusoidal basis functions. Because these basis functions are deterministic and non-learned, they can be computed offline and loaded as static, frozen weight kernels into a standard convolutional layer.  
For a defined Fast Fourier Transform size (![][image1]) and a specific windowing function ![][image2] (such as a Hann window), the discrete Fourier basis kernels for the real (Cosine) and imaginary (Sine) components at any given frequency bin ![][image3] and time ![][image4] are calculated as follows:  
![][image5]  
![][image6]  
By evaluating these equations across all desired frequency bins, the developer constructs a dense weight tensor. A PyTorch nn.Conv1d layer is then instantiated. The in\_channels is set to 1 (representing mono audio input). The out\_channels is set to ![][image7] to yield the concatenated real and imaginary components. The kernel\_size matches the FFT window size (![][image1]), and the stride is set to the designated hop length.  
Similarly, the ISTFT reconstructs the time-domain waveform using an overlap-add synthesis mechanism. This mathematical behavior is structurally identical to the mechanics of nn.ConvTranspose1d. By configuring a transposed convolution layer with identical stride and padding parameters, and loading it with the mathematically derived inverse Fourier basis weights, the overlapping output frames automatically sum together, executing the synthesis natively.

Python  
class LiteRT\_DemucsWrapper(torch.nn.Module):  
    def \_\_init\_\_(self, original\_demucs):  
        super().\_\_init\_\_()  
        self.core\_model \= original\_demucs  
          
        \# Instantiate Convolutional STFT Surrogate  
        self.stft\_surrogate \= torch.nn.Conv1d(...)  
        self.stft\_surrogate.weight.data \= precomputed\_fourier\_basis  
        self.stft\_surrogate.weight.requires\_grad \= False  
          
        \# Instantiate Convolutional ISTFT Surrogate  
        self.istft\_surrogate \= torch.nn.ConvTranspose1d(...)  
        self.istft\_surrogate.weight.data \= precomputed\_inverse\_basis  
        self.istft\_surrogate.weight.requires\_grad \= False

    def forward(self, input\_waveform):  
        \# 1\. Convolutional Spectral Extraction  
        spectral\_data \= self.stft\_surrogate(input\_waveform)  
          
        \# 2\. Derive magnitude and phase natively  
        real, imag \= spectral\_data.chunk(2, dim=1)  
        magnitude \= torch.sqrt(real\*\*2 \+ imag\*\*2 \+ 1e-8)  
          
        \# 3\. Process through the Demucs network backbone  
        separated\_spectral\_stems \= self.core\_model(input\_waveform, magnitude)  
          
        \# 4\. Convolutional Overlap-Add Reconstruction  
        output\_waveforms \= self.istft\_surrogate(separated\_spectral\_stems)  
        return output\_waveforms

By substituting all complex FFT operations with convolutional surrogates, the LiteRT\_DemucsWrapper becomes entirely traceable. Every node within the architecture now resolves to highly optimized, core ATen operators fully supported by the ai\_edge\_torch lowering compiler.

### **Edge Quantization Strategies and Numerical Precision**

To maximize performance on mobile NPUs and minimize the memory footprint of the final .litertlm container, the model must undergo post-training quantization. The ai\_edge\_torch framework incorporates the PT2EQuantizer (PyTorch 2 Export Quantization), which allows for the application of symmetric per-channel quantization prior to the TFLite compilation phase.  
However, the application of integer quantization (INT8) to the Demucs architecture requires immense precision. While the transformer and LSTM components of the dual-path network are highly resilient to quantization noise, the convolutional layers acting as STFT/ISTFT surrogates are exceptionally sensitive. Quantizing the discrete Fourier basis kernels introduces phase alignment errors and truncation noise, which manifest as severe audio artifacts and metallic distortion in the reconstructed waveform.  
The deployment strategy must utilize selective, mixed-precision quantization. The developer utilizes the input\_qspec\_map attribute within the PT2EQuantizer annotation mechanism to explicitly exclude the self.stft\_surrogate and self.istft\_surrogate modules from the quantization process.

Python  
from ai\_edge\_torch.quantize.pt2e\_quantizer import PT2EQuantizer, get\_symmetric\_quantization\_config

quantizer \= PT2EQuantizer()  
quant\_config \= get\_symmetric\_quantization\_config(is\_per\_channel=True, is\_dynamic=False)

\# Exclude Fourier Conv layers from INT8 targeting  
\#... custom input\_qspec\_map annotation logic...

quantizer.set\_global(quant\_config)

\# Execute conversion and quantization  
edge\_demucs\_quantized \= ai\_edge\_torch.convert(  
    model,   
    sample\_inputs,   
    quant\_config=ai\_edge\_torch.quantize.QuantConfig(pt2e\_quantizer=quantizer)  
)

This ensures that the spectral transformations are executed with high dynamic range (FP32 or FP16), preserving the phase fidelity of the audio, while the massive parameter matrices of the separation network are compressed to INT8, maximizing cache locality and NPU instruction throughput.  
Following the successful generation of this mixed-precision flatbuffer, the litert-lm-builder is invoked to construct the final demucs.litertlm artifact. This achieves the ultimate stretch goal: an industry-grade, complex dual-path audio separation network, fully contained within a single deployable file, capable of executing entirely on-device across Android, iOS, and edge Linux hardware without external dependencies, dynamic graph allocations, or host-side signal processing overheads.

## **Conclusion**

The engineering of monolithic AI runtimes requires an exhaustive understanding of computation graphs, compiler mechanics, hardware memory limitations, and mathematical signal processing. As established by the structural blueprint of the Demucs\_v4\_TRT architecture, encapsulating control flows and domain transformations into static, single-graph topologies is the singular method for unlocking aggressive kernel fusion and circumventing the crippling latency bottlenecks inherent to host-orchestrated execution.  
By systematically applying this philosophy, highly complex autoregressive architectures like Chatterbox Turbo can be liberated from the constraints of Python and Node.js GIL synchronization. Translating fragmented token generation sequences into static, chunked ONNX Loop operators enables maximum throughput on hardware compilers like TensorRT. Furthermore, projecting these unified architectures into the edge deployment ecosystem via ai\_edge\_torch and the .litertlm container format democratizes their execution across constrained mobile environments. For architectures dependent on mathematically unsupported operations—such as the discrete Fourier transforms within Demucs—the transmutation of complex spectral functions into standard convolutional primitives serves as a robust, universally compatible mechanism to guarantee graph survival across disparate NPU compilers. Ultimately, the transition from fragmented scripts to compiled monolithic containers represents the definitive maturation path for deploying generative AI models into high-performance, real-world production environments.

[image1]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABMAAAAaCAYAAABVX2cEAAABF0lEQVR4Xu3SvWpCQRAF4JFoYVCMlaSLKQKCYOEzCLGxsAiGlLZiSlOJaGMhiDaCfcgrpPABBB8gL2BlI9glkMRz2LlmvPGvSJd74Gt2x93r7Ij8y1RhbDxB1OxfwcBX04ULU7MJF4tqBZ9QMPsRSMNE1eESzkzNVu5VE5bwAmGzH4ehSpr1nfmzw0LiekA34g7igVlTk4GeYv3e8Ka+isEtfEHL1JThQR1MHhqKScAU3iCla22to4Nhr0rKSw2+oSI//eI/OKlf18oLR2EOr+K+xuvVSf1ir8gLf9SBd3FDerRXDG9lP3aFr8lX5SDnfHu/wtsf4c6/oeGccUxmcqRXfOqFuCZ/wLM6t0XixmTkWwsSJMgma7GJM4ZMgY89AAAAAElFTkSuQmCC>

[image2]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAC4AAAAbCAYAAAADDr0pAAACr0lEQVR4Xu2WS6iNURTHlzyiyDMlJI+JyCMxEGVgwIAkA0WmGBF53RklDKQMZYAhZjcjAzcpSslAUiaUKDIwMDLg/7trb3efdff5PqNz3dv51a/T2fv79rf23ms/zPpMTObJi3JOrAhQPyBnx4oa8+U1eSt4QM6Q50P5KTktGd89I6dbJwRxX24M5d3YYt7m1FgRiR8fN4FPlkvkY/nV/EWcKSeZB3dD/pJ7rHO6eXe9fCWPhLrMuWSE9hkUXFmU803S6nRR1shd+VEuSpYMyJ9yUyiH7fKE+QdLliefpd/INvk5SedL1srncmkor0LgX+SKZGah+Yj+Nh/xElKJaeWZyNHkQzkl1AGz8DI5N9SRboPyYCivQkN5VPPIMopnzYOrBb7P6o0TKAEjayJDe1vN3yHgp0nW07LiOWCWb4eyKrXA15g3sNc88EOpnLzHq+a5GmEE82iWnaVDO+Qx+cN8QJDAF488Nsx+OWTefu0bfyGoPKrIRy7JVeYdoVN5kR1Pkqc1WCOvk7V1sVN+M8/tmN8ZYui25jrgwTJwgiI4KAOnI3QIa7kLbYHn/GZmYn5n/jnw3eaBsxfjdfN0gNXyu7xnnjqkEHajKXAW3iNrz9+JH3hOh3dJdowML9IIB9QF890h7tslPP8+ST6XsAg/2MhCB86CdcV/IPC2dBomBz6YZI/O5MDfWH3PjrALDCXZHUr4zie52fw6gJzMeXYzbKPMDDMUrxEdcOy/Ne89lhDIE3k4lDdxM3k5lNPxF/KK+aGHpGKEVKpdFUbBCG+wehqwe1DXevEp2JXkgInXVNpZYH7XwQh1XBXi+ugJ+eBguuMMtkGHmYlu221P4KZ5xzrXTBPMzgNr3rV6Bgv0pI1OwQgpxHYbF/SYMsvq+VxCPc/9V4zbwPv0GQv+AL/IkwKAVVoYAAAAAElFTkSuQmCC>

[image3]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAwAAAAeCAYAAAAYa/93AAAAzUlEQVR4XmNgGN6AH4hnAfFBKM5DlcYEOUC8Foj3Q/FWIOZAUYEEuIF4DxAXA7EVFMujqEADJGuQAeK7QOyCLoEMWKDYAYjLgfg9lHaGYla4SijghGJPIO4D4odAnMCARwMyaGUgECrogCQNIH+sAeJJ6BK4gCAQnwbiaHQJXECTAeJhG3QJXAAU9qA4AMUFUYBkDUUMkGQBSh5EgTkMBEJIDIjPAfEEKN4OxJYoKtAAKFSeAHE/FM9mIJAMSNbACMT6QOwKxaAEOAooAgC0BSSblUv26gAAAABJRU5ErkJggg==>

[image4]: <data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAcAAAAeCAYAAADgiwSAAAAAm0lEQVR4XmNgGMSAH4pLgFgSTY7BBYofArESmhx+yXIoPgDEPDBBXSAOAeJDULwNiAOAWAQkaQ7EqUD8Hoo7gdgfiIVAkiBgCsR3oVgTJggDeCXTgfg0FAsiSzAC8XwgngPFKACkEqQjGopBAORfWRAD5JUHDBB7QVgGiFuAmJWgJCg0tgJxOxQvBmJ5kAQMMAOxMBSD2KOAKAAAa1sc/QiGhwIAAAAASUVORK5CYII=>

[image5]: <data:image/png;base64,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>

[image6]: <data:image/png;base64,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>

[image7]: <data:image/png;base64,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>