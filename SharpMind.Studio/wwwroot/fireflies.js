(() => {
    const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const count = reducedMotion ? 12 : 32;
    let canvas = null;
    let context = null;
    let width = 0;
    let height = 0;
    let boids = [];
    let attractors = [];
    let lastTime = performance.now();
    let frame = 0;

    const random = (min, max) => min + Math.random() * (max - min);

    function accentRgb() {
        const root = document.querySelector('.studio-root');
        const raw = root ? getComputedStyle(root).getPropertyValue('--accent-rgb').trim() : '';
        const values = raw.split(',').map(value => Number(value.trim()));
        return values.length === 3 && values.every(Number.isFinite) ? values : [94, 234, 212];
    }

    function makeAttractor() {
        return {
            centerX: random(width * 0.2, width * 0.8),
            centerY: random(height * 0.2, height * 0.8),
            radiusX: random(70, Math.max(120, width * 0.22)),
            radiusY: random(50, Math.max(90, height * 0.2)),
            phase: random(0, Math.PI * 2),
            speed: random(0.00035, 0.0011),
            x: width * 0.5,
            y: height * 0.5,
        };
    }

    function makeBoid() {
        return {
            x: random(0, width),
            y: random(0, height),
            vx: random(-0.4, 0.4),
            vy: random(-0.3, 0.3),
            size: random(1.2, 3),
            phase: random(0, Math.PI * 2),
            pulse: random(0.007, 0.024),
            mass: random(0.7, 1.5),
            maxSpeed: random(0.5, 0.92),
            personalSpace: random(18, 44),
            alignment: random(0.025, 0.065),
            cohesion: random(0.00035, 0.0011),
            separation: random(0.1, 0.24),
            orbit: random(-1, 1),
            home: Math.floor(random(0, 3)),
            switchTimer: random(3, 11),
        };
    }

    function resize() {
        if (!canvas || !context) return;
        width = window.innerWidth;
        height = window.innerHeight;
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        canvas.width = Math.max(1, Math.floor(width * dpr));
        canvas.height = Math.max(1, Math.floor(height * dpr));
        context.setTransform(dpr, 0, 0, dpr, 0, 0);

        attractors = Array.from({ length: 3 }, makeAttractor);
        for (const attractor of attractors) {
            attractor.x = attractor.centerX;
            attractor.y = attractor.centerY;
        }

        boids = Array.from({ length: count }, makeBoid);
        if (reducedMotion) draw(0);
    }

    function updateAttractors(step) {
        for (const attractor of attractors) {
            attractor.phase += attractor.speed * step;
            attractor.x = attractor.centerX + Math.cos(attractor.phase) * attractor.radiusX;
            attractor.y = attractor.centerY + Math.sin(attractor.phase * 1.27) * attractor.radiusY;
            attractor.x = Math.max(40, Math.min(width - 40, attractor.x));
            attractor.y = Math.max(40, Math.min(height - 40, attractor.y));
        }
    }

    function update(delta) {
        const perception = 145;
        const margin = 100;
        const step = Math.min(delta / 16.6667, 2.5);

        updateAttractors(step);

        for (const boid of boids) {
            let alignX = 0;
            let alignY = 0;
            let pullX = 0;
            let pullY = 0;
            let pullWeight = 0;
            let separateX = 0;
            let separateY = 0;
            let swirlX = 0;
            let swirlY = 0;
            let neighbors = 0;
            let close = 0;

            for (const other of boids) {
                if (other === boid) continue;
                const dx = other.x - boid.x;
                const dy = other.y - boid.y;
                const distance = Math.hypot(dx, dy);
                if (distance <= 0 || distance >= perception) continue;

                alignX += other.vx;
                alignY += other.vy;
                neighbors += 1;

                const gravity = 1 / Math.max(distance, 28);
                pullX += dx * gravity;
                pullY += dy * gravity;
                pullWeight += gravity;

                if (distance < boid.personalSpace) {
                    const push = 1 - distance / boid.personalSpace;
                    separateX -= (dx / distance) * push;
                    separateY -= (dy / distance) * push;
                    swirlX -= (dy / distance) * push;
                    swirlY += (dx / distance) * push;
                    close += 1;
                }
            }

            let accelerationX = 0;
            let accelerationY = -boid.vy * 0.0025;

            if (neighbors > 0) {
                accelerationX += ((alignX / neighbors) - boid.vx) * boid.alignment;
                accelerationY += ((alignY / neighbors) - boid.vy) * boid.alignment;

                accelerationX += (pullX / pullWeight) * boid.cohesion;
                accelerationY += (pullY / pullWeight) * boid.cohesion;
            }

            if (close > 0) {
                accelerationX += (separateX / close) * boid.separation;
                accelerationY += (separateY / close) * boid.separation;
                accelerationX += (swirlX / close) * boid.orbit * 0.028;
                accelerationY += (swirlY / close) * boid.orbit * 0.028;
            }

            const home = attractors[boid.home % attractors.length];
            const homeX = home.x - boid.x;
            const homeY = home.y - boid.y;
            const homeDistance = Math.max(1, Math.hypot(homeX, homeY));
            const homePull = 0.00024 / boid.mass;
            accelerationX += (homeX / homeDistance) * Math.min(homeDistance, 320) * homePull;
            accelerationY += (homeY / homeDistance) * Math.min(homeDistance, 320) * homePull;
            accelerationX += (-homeY / homeDistance) * boid.orbit * 0.00022;
            accelerationY += (homeX / homeDistance) * boid.orbit * 0.00022;

            if (boid.x < margin) accelerationX += ((margin - boid.x) / margin) * 0.06;
            if (boid.x > width - margin) accelerationX -= ((boid.x - (width - margin)) / margin) * 0.06;
            if (boid.y < margin) accelerationY += ((margin - boid.y) / margin) * 0.06;
            if (boid.y > height - margin) accelerationY -= ((boid.y - (height - margin)) / margin) * 0.06;

            accelerationX += Math.cos(boid.phase * 0.63) * 0.0016;
            accelerationY += Math.sin(boid.phase * 0.51) * 0.0013;

            boid.vx += (accelerationX / boid.mass) * step;
            boid.vy += (accelerationY / boid.mass) * step;

            const speed = Math.hypot(boid.vx, boid.vy);
            if (speed > boid.maxSpeed) {
                boid.vx = (boid.vx / speed) * boid.maxSpeed;
                boid.vy = (boid.vy / speed) * boid.maxSpeed;
            } else if (speed < 0.1) {
                boid.vx += Math.cos(boid.phase) * 0.012 * step;
                boid.vy += Math.sin(boid.phase) * 0.012 * step;
            }

            boid.x += boid.vx * step;
            boid.y += boid.vy * step;

            boid.x = Math.max(-20, Math.min(width + 20, boid.x));
            boid.y = Math.max(-20, Math.min(height + 20, boid.y));

            boid.phase += boid.pulse * step;
            boid.switchTimer -= step / 60;
            if (boid.switchTimer <= 0) {
                if (Math.random() < 0.45) {
                    let nextHome = Math.floor(random(0, attractors.length));
                    if (nextHome === boid.home) nextHome = (nextHome + 1) % attractors.length;
                    boid.home = nextHome;
                    boid.orbit = random(-1, 1);
                    boid.maxSpeed = random(0.5, 0.92);
                }
                boid.switchTimer = random(4, 13);
            }
        }
    }

    function draw(delta) {
        if (!canvas || !context) return;
        update(delta);
        context.clearRect(0, 0, width, height);
        context.globalCompositeOperation = 'lighter';

        const [red, green, blue] = accentRgb();

        for (const boid of boids) {
            const pulse = 0.55 + 0.45 * Math.sin(boid.phase);
            const alpha = 0.12 + pulse * 0.42;

            context.beginPath();
            context.fillStyle = `rgba(${red}, ${green}, ${blue}, ${alpha})`;
            context.shadowColor = `rgba(${red}, ${green}, ${blue}, ${0.35 + pulse * 0.35})`;
            context.shadowBlur = 9 + pulse * 8;
            context.arc(boid.x, boid.y, boid.size * (0.85 + pulse * 0.25), 0, Math.PI * 2);
            context.fill();
        }

        context.shadowBlur = 0;
        context.globalCompositeOperation = 'source-over';
    }

    function animate(now) {
        const delta = now - lastTime;
        lastTime = now;
        draw(delta);
        frame = window.requestAnimationFrame(animate);
    }

    function attach() {
        const next = document.querySelector('.firefly-canvas');
        if (!next || next === canvas) return;

        if (frame) {
            window.cancelAnimationFrame(frame);
            frame = 0;
        }

        canvas = next;
        context = canvas.getContext('2d');
        if (!context) return;

        resize();
        lastTime = performance.now();
        if (!reducedMotion) frame = window.requestAnimationFrame(animate);
    }

    attach();
    new MutationObserver(attach).observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('load', attach);
    window.addEventListener('resize', resize, { passive: true });
    window.addEventListener('orientationchange', resize, { passive: true });

    document.addEventListener('visibilitychange', () => {
        if (reducedMotion) return;
        if (document.hidden) {
            window.cancelAnimationFrame(frame);
            frame = 0;
        } else {
            lastTime = performance.now();
            frame = window.requestAnimationFrame(animate);
        }
    });
})();


