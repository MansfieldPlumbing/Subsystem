precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    mat2 rot = mat2(1.6, 1.2, -1.2, 1.6);
    for (int i = 0; i < 6; i++) {
        v += a * noise(p);
        p = rot * p * 2.0;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    uv.x *= u_resolution.x / u_resolution.y;

    vec2 shift = vec2(u_time * 0.05, u_time * 0.02);
    
    float n = fbm(uv * 3.0 + shift);
    
    // Create layers of clouds
    float cloud1 = fbm(uv * 2.0 + shift * 1.2 + n * 0.5);
    float cloud2 = fbm(uv * 4.0 - shift * 0.8 + n * 0.2);
    
    float finalNoise = mix(cloud1, cloud2, 0.5);
    
    // Sky gradient
    vec3 skyTop = vec3(0.2, 0.4, 0.8);
    vec3 skyBottom = vec3(0.5, 0.7, 0.9);
    vec3 skyColor = mix(skyBottom, skyTop, uv.y);
    
    // Cloud color with shading
    vec3 cloudBase = vec3(1.0, 1.0, 1.0);
    vec3 cloudShadow = vec3(0.7, 0.7, 0.8);
    vec3 cloudColor = mix(cloudShadow, cloudBase, clamp(n * 1.5, 0.0, 1.0));
    
    // Alpha blending simulation
    float density = smoothstep(0.4, 0.8, finalNoise);
    vec3 finalColor = mix(skyColor, cloudColor, density);
    
    // Simple sun glow
    float sun = 0.01 / length(uv - vec2(1.2, 0.8));
    finalColor += vec3(1.0, 0.9, 0.7) * sun;

    gl_FragColor = vec4(finalColor, 1.0);
}
