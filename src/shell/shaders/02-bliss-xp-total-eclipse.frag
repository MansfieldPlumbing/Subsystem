precision mediump float;

uniform vec2 u_resolution;
uniform float u_time;

vec3 getSkyColor(vec2 uv) {
    vec3 dayBlue = vec3(0.4, 0.6, 0.9);
    vec3 dayGreen = vec3(0.2, 0.8, 0.3);
    vec3 eclipseDark = vec3(0.02, 0.05, 0.1);
    
    float hill = sin(uv.x * 2.0 + 1.0) * 0.2 - 0.5;
    float mask = smoothstep(hill, hill + 0.01, uv.y);
    
    vec3 sky = mix(eclipseDark, dayBlue * 0.2, uv.y + 0.5);
    vec3 ground = mix(vec3(0.05, 0.15, 0.05), dayGreen * 0.1, uv.y + 1.0);
    
    return mix(ground, sky, mask);
}

void main() {
    vec2 uv = (gl_FragCoord.xy - 0.5 * u_resolution.xy) / min(u_resolution.y, u_resolution.x);
    
    // Moon and Sun positions
    vec2 sunPos = vec2(0.0, 0.2);
    float dist = length(uv - sunPos);
    
    // Eclipse timing
    float cycle = sin(u_time * 0.5) * 0.5 + 0.5;
    vec2 moonOffset = vec2((cycle - 0.5) * 0.15, 0.0);
    float moonDist = length(uv - (sunPos + moonOffset));
    
    // Corona Effect
    float corona = 0.05 / (dist - 0.18);
    corona *= smoothstep(0.18, 0.5, dist);
    vec3 coronaColor = vec3(0.8, 0.9, 1.0) * corona * (1.0 - smoothstep(0.0, 0.2, abs(cycle - 0.5)));
    
    // Baily's Beads / Diamond Ring
    float angle = atan(uv.y - sunPos.y, uv.x - sunPos.x);
    float beads = pow(max(0.0, sin(angle * 10.0 + u_time)), 20.0) * 0.01;
    float diamond = smoothstep(0.02, 0.0, abs(cycle - 0.53)) * pow(max(0.0, 1.0 - length(uv - (sunPos + vec2(0.18, 0.0)))), 50.0) * 5.0;

    // Bliss Landscape Colors
    vec3 scene = getSkyColor(uv);
    
    // Sun Disk
    float sunDisk = smoothstep(0.2, 0.19, dist);
    vec3 sunColor = vec3(1.0, 0.95, 0.8) * sunDisk;
    
    // Moon Disk (Shadow)
    float moonDisk = smoothstep(0.195, 0.2, moonDist);
    
    // Compositing
    vec3 finalColor = scene;
    
    // Add atmospheric glow
    finalColor += coronaColor * 0.5;
    
    // Apply eclipse shadow to the sun
    vec3 celestial = mix(sunColor, vec3(0.0), 1.0 - moonDisk);
    finalColor += celestial;
    finalColor += diamond * vec3(1.0, 0.9, 0.7);
    
    // Starfield during totality
    float stars = pow(fract(sin(dot(uv, vec2(12.9898, 78.233))) * 43758.5453), 20.0);
    float starsVisibility = smoothstep(0.2, 0.0, abs(cycle - 0.5));
    finalColor += stars * starsVisibility;

    gl_FragColor = vec4(finalColor, 1.0);
}
