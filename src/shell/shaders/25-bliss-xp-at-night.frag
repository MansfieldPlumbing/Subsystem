precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i + vec2(0.0, 0.0)), hash(i + vec2(1.0, 0.0)), u.x),
               mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 5; i++) {
        v += a * noise(p);
        p *= 2.0;
        a *= 0.5;
    }
    return v;
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    vec2 p = (gl_FragCoord.xy * 2.0 - u_resolution.xy) / min(u_resolution.y, u_resolution.x);
    
    // Sky gradient
    vec3 skyTop = vec3(0.01, 0.02, 0.08);
    vec3 skyBottom = vec3(0.05, 0.1, 0.25);
    vec3 color = mix(skyBottom, skyTop, uv.y);
    
    // Stars
    float stars = pow(hash(uv * 500.0), 100.0);
    stars *= step(0.999, hash(uv + u_time * 0.001));
    color += stars * (0.5 + 0.5 * sin(u_time + uv.x * 10.0));
    
    // Moon
    vec2 moonPos = vec2(0.6, 0.6);
    float moonDist = length(p - moonPos);
    float moon = smoothstep(0.15, 0.145, moonDist);
    vec3 moonColor = vec3(0.9, 0.95, 1.0);
    float glow = exp(-moonDist * 4.0) * 0.4;
    color += moonColor * moon;
    color += moonColor * glow;
    
    // Moon crater/texture
    if (moonDist < 0.15) {
        float mNoise = fbm(p * 15.0);
        color = mix(color, color * (0.8 + 0.2 * mNoise), 0.5);
    }
    
    // Bliss Hills (Layer 1 - Distant)
    float h1 = 0.2 * sin(uv.x * 2.5 + 1.2) + 0.1 * cos(uv.x * 4.0) + 0.25;
    float hill1Mask = smoothstep(h1, h1 - 0.01, uv.y);
    vec3 hill1Color = vec3(0.0, 0.05, 0.1);
    color = mix(color, hill1Color, hill1Mask);
    
    // Bliss Hills (Layer 2 - Iconic foreground)
    float h2 = 0.25 * sin(uv.x * 1.8 - 0.5) + 0.15 * cos(uv.x * 1.2) + 0.2;
    // Add subtle wave for grass feel
    h2 += 0.005 * sin(uv.x * 50.0 + u_time); 
    float hill2Mask = smoothstep(h2, h2 - 0.01, uv.y);
    
    // Lighting on the hill from the moon
    float slope = (h2 - (0.25 * sin((uv.x+0.01) * 1.8 - 0.5) + 0.15 * cos((uv.x+0.01) * 1.2) + 0.2)) / 0.01;
    float rimLight = smoothstep(0.0, 1.0, slope * 0.5 + 0.5);
    
    vec3 hill2Base = vec3(0.02, 0.12, 0.05);
    vec3 hill2Highlight = vec3(0.1, 0.3, 0.2);
    vec3 hill2Final = mix(hill2Base, hill2Highlight, rimLight * 0.4);
    
    // Add a bit of blue moon sheen
    hill2Final += vec3(0.05, 0.1, 0.2) * (1.0 - uv.y / h2);
    
    color = mix(color, hill2Final, hill2Mask);
    
    // Subtle fog near the base
    float fog = exp(-uv.y * 3.0) * 0.15;
    color += vec3(0.05, 0.08, 0.15) * fog;

    // Vignette
    float vignette = uv.x * uv.y * (1.0 - uv.x) * (1.0 - uv.y);
    color *= pow(vignette * 15.0, 0.2);

    gl_FragColor = vec4(color, 1.0);
}
