precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

float hash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

void main() {
    vec2 uv = (gl_FragCoord.xy - 0.5 * u_resolution.xy) / min(u_resolution.y, u_resolution.x);
    
    // Background Gradient (XP Bliss style)
    vec3 skyTop = vec3(0.1, 0.4, 0.8);
    vec3 skyBottom = vec3(0.5, 0.8, 1.0);
    vec3 grassColor = vec3(0.2, 0.6, 0.1);
    
    float hillHeight = sin(uv.x * 2.0 + 1.0) * 0.2 - 0.3;
    float hillMask = smoothstep(hillHeight + 0.01, hillHeight, uv.y);
    
    vec3 bgColor = mix(skyBottom, skyTop, uv.y + 0.5);
    bgColor = mix(bgColor, grassColor * (uv.y + 0.8), hillMask);

    // Rain setup
    vec2 rainUV = uv * vec2(30.0, 1.0);
    float columnId = floor(rainUV.x);
    float speed = hash(vec2(columnId, 123.0)) * 2.0 + 2.0;
    float yPos = fract(uv.y - u_time * speed + hash(vec2(columnId, 456.0)));
    
    // Rain droplet shape
    float rainLine = smoothstep(0.1, 0.0, abs(fract(rainUV.x) - 0.5));
    float rainFade = smoothstep(0.0, 0.5, yPos) * smoothstep(1.0, 0.8, yPos);
    float rainStretch = 0.15;
    float droplet = rainLine * rainFade * smoothstep(rainStretch, 0.0, abs(yPos - 0.5));
    
    // Mist/Atmosphere
    float mist = sin(uv.x * 3.0 + u_time) * 0.1 + 0.1;
    vec3 mistColor = vec3(0.8, 0.9, 1.0) * (uv.y + 0.5);
    
    // Final Composition
    vec3 rainColor = vec3(0.8, 0.9, 1.0) * droplet * 0.6;
    vec3 finalColor = bgColor + rainColor + (mistColor * 0.15);
    
    // Vignette
    finalColor *= 1.0 - dot(uv, uv) * 0.2;

    gl_FragColor = vec4(finalColor, 1.0);
}
