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
    
    float t = u_time * 0.2;
    
    // Sky gradient
    vec3 skyTop = vec3(0.2, 0.5, 0.9);
    vec3 skyBottom = vec3(0.5, 0.8, 1.0);
    vec3 color = mix(skyBottom, skyTop, uv.y);
    
    // Clouds
    float cloudPattern = fbm(p * 0.8 + vec2(t * 0.5, 0.0));
    color = mix(color, vec3(1.0), smoothstep(0.5, 0.8, cloudPattern) * 0.4);
    
    // Distant Hill
    float hill2Height = 0.2 + 0.15 * sin(p.x * 0.5 + t * 0.3) + 0.1 * noise(vec2(p.x * 0.8, t));
    float hill2Mask = smoothstep(hill2Height + 0.01, hill2Height, p.y + 0.2);
    vec3 hill2Color = mix(vec3(0.2, 0.6, 0.3), vec3(0.4, 0.8, 0.5), p.y + 0.5);
    color = mix(color, hill2Color * 0.8, hill2Mask);
    
    // Main Hill (Bliss style)
    float hill1Height = -0.1 + 0.25 * cos(p.x * 0.7 - t * 0.2) + 0.05 * sin(p.x * 1.5 + t);
    float hill1Mask = smoothstep(hill1Height + 0.01, hill1Height, p.y + 0.4);
    
    // Greenery shading
    float grass = noise(p * 15.0 + vec2(t, 0.0));
    vec3 hill1Color = mix(vec3(0.1, 0.4, 0.1), vec3(0.3, 0.7, 0.2), uv.y + grass * 0.2);
    hill1Color *= 0.8 + 0.2 * sin(p.x * 2.0 + t); // Sunlight variation
    
    color = mix(color, hill1Color, hill1Mask);
    
    // Atmospheric perspective
    color = mix(color, vec3(0.7, 0.85, 1.0), (1.0 - uv.y) * 0.15);
    
    gl_FragColor = vec4(color, 1.0);
}
