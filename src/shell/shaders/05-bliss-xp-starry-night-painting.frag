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

vec3 getBrushStroke(vec2 uv, vec3 color1, vec3 color2, float scale) {
    float n = noise(uv * scale + u_time * 0.1);
    float stroke = sin(uv.x * scale + n * 5.0);
    return mix(color1, color2, stroke * 0.5 + 0.5);
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    float aspect = u_resolution.x / u_resolution.y;
    vec2 p = uv;
    p.x *= aspect;

    // Rolling Hills Shape
    float hill1 = 0.35 + 0.12 * sin(p.x * 1.5 + 0.5) + 0.05 * cos(p.x * 4.0 + u_time * 0.1);
    float hill2 = 0.25 + 0.10 * cos(p.x * 2.0 - 1.2) + 0.08 * sin(p.x * 3.5 + u_time * 0.05);
    
    vec3 finalColor = vec3(0.0);

    // Sky Background (Starry Night palette)
    vec3 skyTop = vec3(0.05, 0.1, 0.4);
    vec3 skyBottom = vec3(0.1, 0.4, 0.8);
    vec3 sky = mix(skyBottom, skyTop, uv.y);

    // Van Gogh Swirls in Sky
    vec2 swirlPos = vec2(0.7 * aspect, 0.75);
    vec2 relP = p - swirlPos;
    float dist = length(relP);
    float angle = atan(relP.y, relP.x);
    float swirl = sin(dist * 15.0 - u_time * 0.5 - angle * 4.0);
    sky += vec3(0.8, 0.7, 0.2) * (max(0.0, swirl) * exp(-dist * 4.0));

    // Stars
    for(int i = 0; i < 8; i++) {
        vec2 starPos = vec2(hash(vec2(float(i), 1.0)) * aspect, hash(vec2(float(i), 2.0)) * 0.4 + 0.5);
        float starDist = length(p - starPos);
        float starGlow = exp(-starDist * 15.0);
        float starCore = smoothstep(0.015, 0.0, starDist);
        float starPulse = 0.8 + 0.2 * sin(u_time + float(i));
        sky += (vec3(1.0, 0.9, 0.3) * starGlow + vec3(1.0) * starCore) * starPulse;
        
        // Halo swirls around stars
        float halo = sin(starDist * 30.0 - u_time * 2.0);
        sky += vec3(0.3, 0.4, 0.7) * max(0.0, halo) * exp(-starDist * 10.0);
    }

    // Hill Colors (Bliss XP Green palette with brush strokes)
    vec3 hillColor1 = getBrushStroke(p * 2.0, vec3(0.1, 0.5, 0.1), vec3(0.4, 0.8, 0.2), 10.0);
    vec3 hillColor2 = getBrushStroke(p * 2.5, vec3(0.05, 0.3, 0.05), vec3(0.3, 0.6, 0.1), 15.0);
    
    // Mix elements based on height
    if (uv.y < hill1) {
        finalColor = hillColor1;
        // Shadow/Depth on hill 1
        finalColor *= smoothstep(hill1, hill1 - 0.2, uv.y) * 0.5 + 0.5;
    } else {
        finalColor = sky;
    }
    
    if (uv.y < hill2) {
        finalColor = mix(finalColor, hillColor2, 1.0);
        finalColor *= smoothstep(hill2, hill2 - 0.1, uv.y) * 0.3 + 0.7;
    }

    // Subtle atmospheric brush texture over everything
    float grain = noise(p * 200.0 + u_time);
    finalColor += (grain - 0.5) * 0.05;

    // Vignette
    float vignette = smoothstep(1.5, 0.4, length(uv - 0.5));
    finalColor *= vignette;

    gl_FragColor = vec4(finalColor, 1.0);
}
