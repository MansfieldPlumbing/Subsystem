precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i + vec2(0,0)), hash(i + vec2(1,0)), f.x),
               mix(hash(i + vec2(0,1)), hash(i + vec2(1,1)), f.x), f.y);
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
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Sky gradient
    vec3 skyTop = vec3(0.1, 0.4, 0.85);
    vec3 skyBottom = vec3(0.5, 0.8, 1.0);
    vec3 color = mix(skyBottom, skyTop, uv.y);

    // Clouds
    vec2 cloudUV = p * 1.5;
    cloudUV.x -= u_time * 0.02;
    float clouds = fbm(cloudUV + fbm(cloudUV * 0.5));
    color = mix(color, vec3(1.0), smoothstep(0.4, 0.8, clouds * (uv.y + 0.2)));

    // Hill 1 (Back)
    float h1 = 0.35 + 0.12 * sin(p.x * 1.2 + 0.5) + 0.05 * cos(p.x * 2.8);
    vec3 grassBack = vec3(0.2, 0.5, 0.1);
    float mask1 = smoothstep(h1, h1 - 0.005, uv.y);
    
    // Hill 2 (Middle)
    float h2 = 0.25 + 0.15 * sin(p.x * 0.8 - 1.0) + 0.08 * sin(p.x * 2.1 + u_time * 0.05);
    vec3 grassMid = vec3(0.3, 0.65, 0.15);
    float mask2 = smoothstep(h2, h2 - 0.005, uv.y);
    
    // Hill 3 (Front)
    float h3 = 0.15 + 0.1 * cos(p.x * 1.5 + 2.0) + 0.05 * sin(p.x * 4.0);
    vec3 grassFront = vec3(0.4, 0.75, 0.2);
    float mask3 = smoothstep(h3, h3 - 0.005, uv.y);

    // Apply textures and lighting to hills
    float detail = noise(p * 20.0);
    grassBack *= 0.8 + 0.2 * detail;
    grassMid *= 0.9 + 0.2 * noise(p * 15.0);
    grassFront *= 1.0 + 0.2 * noise(p * 10.0);

    // Layering
    color = mix(color, grassBack, mask1);
    color = mix(color, grassMid, mask2);
    color = mix(color, grassFront, mask3);

    // Sun glow
    float sun = 1.0 - distance(uv, vec2(0.8, 0.8));
    color += vec3(1.0, 0.9, 0.6) * pow(max(0.0, sun), 4.0) * 0.3;

    // Final color grading
    color *= 1.1; // Brighten
    color = pow(color, vec3(0.95)); // Slightly more contrast

    gl_FragColor = vec4(color, 1.0);
}
