precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

void main() {
    vec2 uv = gl_FragCoord.xy / u_resolution.xy;
    vec2 p = (gl_FragCoord.xy - 0.5 * u_resolution.xy) / min(u_resolution.y, u_resolution.x);
    
    // Create the Bliss-like rolling hill curve
    float hill = sin(uv.x * 3.0 + 1.2) * 0.2 + 0.3;
    float hillMask = smoothstep(hill + 0.01, hill, uv.y);
    
    // Sky Gradient (Deep Night to Twilight Blue)
    vec3 skyTop = vec3(0.02, 0.05, 0.2);
    vec3 skyBottom = vec3(0.1, 0.3, 0.6);
    vec3 skyColor = mix(skyBottom, skyTop, uv.y);
    
    // Starry Night Sparkles
    vec2 starPos = p * 15.0;
    vec2 id = floor(starPos);
    vec2 gv = fract(starPos) - 0.5;
    float h = hash(id);
    float size = (sin(u_time * 2.0 + h * 10.0) * 0.5 + 0.5) * 0.4;
    float star = smoothstep(size, 0.0, length(gv));
    vec3 stars = vec3(star) * step(hill, uv.y) * h;
    
    // Swirling Van Gogh clouds
    float angle = atan(p.y - 0.5, p.x);
    float dist = length(p - vec2(0.3, 0.6));
    float swirl = sin(dist * 10.0 - u_time + angle * 3.0);
    vec3 cloudColor = vec3(0.3, 0.4, 0.8) * smoothstep(0.8, 1.0, swirl) * 0.3;
    skyColor += cloudColor * step(hill, uv.y);

    // Green Hills (Bliss aesthetic)
    vec3 hillDark = vec3(0.05, 0.2, 0.05);
    vec3 hillLight = vec3(0.1, 0.4, 0.1);
    float texture = sin(uv.x * 50.0) * sin(uv.y * 50.0) * 0.05;
    vec3 greenHill = mix(hillDark, hillLight, uv.y / hill) + texture;
    
    // Distant Highlight on the ridge
    float ridge = smoothstep(0.02, 0.0, abs(uv.y - hill));
    greenHill += ridge * 0.1;

    // Final Composition
    vec3 finalColor = mix(skyColor + stars, greenHill, hillMask);
    
    // Subtle Vignette
    finalColor *= 1.0 - dot(p, p) * 0.2;

    gl_FragColor = vec4(finalColor, 1.0);
}
