// fp32 in-place leaky_relu — composes with the fp32 GEMM output (no fp16 hop).
cbuffer P { uint n; float alpha; };
RWByteAddressBuffer X;   // u0, in-place
[numthreads(64, 1, 1)]
void main(uint3 t : SV_DispatchThreadID)
{
    if (t.x >= n) return;
    float v = X.Load<float>(t.x * 4);
    X.Store<float>(t.x * 4, v > 0.0f ? v : v * alpha);
}
