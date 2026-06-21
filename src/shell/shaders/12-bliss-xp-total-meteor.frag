precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(float n) {
    return fract(sin(n) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i.x + i.y * 57.0);
    float b = hash(i.x + 1.0 + i.y * 57.0);
    float c = hash(i.x + (i.y + 1.0) * 57.0);
    float d = hash(i.x + 1.0 + (i.y + 1.0) * 57.0);
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Sky - Classic Bliss Blue
    vec3 skyTop = vec3(0.1, 0.4, 0.8);
    vec3 skyBottom = vec3(0.5, 0.8, 1.0);
    vec3 color = mix(skyBottom, skyTop, uv.y);

    // Clouds (Subtle)
    float cloud = noise(p * 3.0 + u_time * 0.05);
    color += vec3(1.0) * smoothstep(0.6, 0.9, cloud) * 0.2;

    // Meteors
    for(float i = 0.0; i < 12.0; i++) {
        float speed = 0.8 + hash(i * 1.2) * 1.5;
        float t = fract(u_time * 0.2 * speed + hash(i * 15.6));
        
        // Random diagonal paths
        vec2 start = vec2(hash(i * 4.5) * aspect * 1.5, 1.2);
        vec2 dir = vec2(-1.2, -0.8);
        vec2 meteorPos = start + dir * t * 2.5;
        
        // Distance to the meteor line
        vec2 pa = p - meteorPos;
        vec2 ba = -dir * 0.5; // length of tail
        float h = clamp(dot(pa, ba) / dot(ba, ba), 0.0, 1.0);
        float d = length(pa - ba * h);
        
        // Meteor head and tail
        float intensity = exp(-d * 60.0) * (1.0 - h);
        intensity += exp(-d * 300.0) * 2.0 * (1.0 - h); // core glow
        
        vec3 meteorColor = mix(vec3(0.7, 0.9, 1.0), vec3(1.0, 0.4, 0.2), h);
        color += meteorColor * intensity * step(t, 0.9) * step(0.1, t);
    }

    // Lush Green Hills
    float h1 = 0.35 + 0.15 * sin(p.x * 1.2 + 1.0) + 0.05 * cos(p.x * 3.5 + u_time * 0.1);
    float h2 = 0.20 + 0.10 * cos(p.x * 1.8 - 0.5) + 0.03 * sin(p.x * 5.0 - u_time * 0.05);
    float h3 = 0.10 + 0.08 * sin(p.x * 0.8 + 2.5);

    // Hill Colors (XP Vibrant Greens)
    vec3 green1 = vec3(0.2, 0.7, 0.15);
    vec3 green2 = vec3(0.15, 0.6, 0.1);
    vec3 green3 = vec3(0.1, 0.5, 0.05);

    // Shadowing and detail on hills
    float detail1 = noise(p * 10.0) * 0.05;
    float detail2 = noise(p * 15.0) * 0.04;

    // Layering
    if (uv.y < h1 + detail1) {
        float grad = (uv.y / h1);
        color = mix(green1 * 0.6, green1 * 1.2, grad);
    }
    if (uv.y < h2 + detail2) {
        float grad = (uv.y / h2);
        color = mix(green2 * 0.5, green2 * 1.1, grad);
    }
    if (uv.y < h3) {
        float grad = (uv.y / h3);
        color = mix(green3 * 0.4, green3 * 1.0, grad);
    }

    // Final Post-FX
    color *= 1.1; // Brighten
    color += noise(p * 500.0) * 0.02; // Slight film grain

    gl_FragColor = vec4(color, 1.0);
}
