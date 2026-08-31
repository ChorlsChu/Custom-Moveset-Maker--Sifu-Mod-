// Sifu Moveset Editor - Three.js Skeleton + Mesh Viewer

let scene, camera, renderer, skeletonHelper, mixer;
let clock = new THREE.Clock();
let currentAction = null;
let animationData = null;
let isPlaying = false;
let playbackSpeed = 1.0;
let bones = [];
let boneMap = {};
let rootBone = null;
let currentMesh = null;
let currentSkeleton = null;
let orbitSpherical = { theta: 0, phi: Math.PI / 3, radius: 3 };
let orbitTarget = new THREE.Vector3(0, 0.8, 0);
let updateOrbitCamera = null;
let cameraInitialized = false;
let lastLoadedCharacter = "";
function init() {
    const container = document.getElementById('container');

    scene = new THREE.Scene();
    scene.background = new THREE.Color(0x11111b);

    camera = new THREE.PerspectiveCamera(45, container.clientWidth / container.clientHeight, 0.1, 10000);
    camera.position.set(0, 1.2, 3);
    camera.lookAt(0, 0.8, 0);

    renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setSize(container.clientWidth, container.clientHeight);
    renderer.setPixelRatio(window.devicePixelRatio);
    container.appendChild(renderer.domElement);

    const grid = new THREE.GridHelper(4, 20, 0x313244, 0x1e1e2e);
    scene.add(grid);

    const axes = new THREE.AxesHelper(0.5);
    axes.position.set(-1.5, 0.01, -1.5);
    scene.add(axes);

    const ambient = new THREE.AmbientLight(0xffffff, 1.0);
    scene.add(ambient);

    const dirLight = new THREE.DirectionalLight(0xffffff, 0.8);
    dirLight.position.set(5, 10, 5);
    scene.add(dirLight);

    setupOrbitControls();

    window.addEventListener('resize', onResize);
    animate();
}

function setupOrbitControls() {
    let isDragging = false;
    let previousMouse = { x: 0, y: 0 };

    function updateCamera() {
        camera.position.x = orbitTarget.x + orbitSpherical.radius * Math.sin(orbitSpherical.phi) * Math.sin(orbitSpherical.theta);
        camera.position.y = orbitTarget.y + orbitSpherical.radius * Math.cos(orbitSpherical.phi);
        camera.position.z = orbitTarget.z + orbitSpherical.radius * Math.sin(orbitSpherical.phi) * Math.cos(orbitSpherical.theta);
        camera.lookAt(orbitTarget);
    }

    updateOrbitCamera = updateCamera;

    const canvas = renderer.domElement;

    canvas.addEventListener('mousedown', (e) => {
        isDragging = true;
        previousMouse = { x: e.clientX, y: e.clientY };
    });

    canvas.addEventListener('mousemove', (e) => {
        if (!isDragging) return;
        const dx = e.clientX - previousMouse.x;
        const dy = e.clientY - previousMouse.y;

        if (e.buttons === 1) {
            orbitSpherical.theta -= dx * 0.005;
            orbitSpherical.phi = Math.max(0.1, Math.min(Math.PI - 0.1, orbitSpherical.phi - dy * 0.005));
        } else if (e.buttons === 2) {
            const panSpeed = 0.002 * orbitSpherical.radius;
            orbitTarget.x -= dx * panSpeed * Math.cos(orbitSpherical.theta);
            orbitTarget.z += dx * panSpeed * Math.sin(orbitSpherical.theta);
            orbitTarget.y += dy * panSpeed;
        }

        previousMouse = { x: e.clientX, y: e.clientY };
        updateCamera();
    });

    canvas.addEventListener('mouseup', () => { isDragging = false; });
    canvas.addEventListener('mouseleave', () => { isDragging = false; });

    canvas.addEventListener('wheel', (e) => {
        const clampedDelta = Math.max(-120, Math.min(120, e.deltaY));
        const zoomFactor = Math.pow(1.001, clampedDelta);
        orbitSpherical.radius = Math.max(0.3, Math.min(1000, orbitSpherical.radius * zoomFactor));
        updateCamera();
        e.preventDefault();
    }, { passive: false });

    canvas.addEventListener('contextmenu', (e) => e.preventDefault());

    updateCamera();
}

function onResize() {
    const container = document.getElementById('container');
    camera.aspect = container.clientWidth / container.clientHeight;
    camera.updateProjectionMatrix();
    renderer.setSize(container.clientWidth, container.clientHeight);
}

function animate() {
    requestAnimationFrame(animate);

    if (mixer && isPlaying) {
        const delta = clock.getDelta();
        mixer.update(delta * playbackSpeed);

        if (currentAction) {
            const time = currentAction.time;
            const duration = currentAction.getClip().duration;
            const progress = duration > 0 ? time / duration : 0;

            window.chrome.webview.postMessage(JSON.stringify({
                action: 'timeUpdate',
                time: time,
                duration: duration,
                progress: progress
            }));
        }
    }

    renderer.render(scene, camera);
}

function clearMesh() {
    if (currentMesh) {
        scene.remove(currentMesh);
        currentMesh.geometry.dispose();
        if (currentMesh.material) currentMesh.material.dispose();
        currentMesh = null;
    }
    currentSkeleton = null;
}

function loadMesh(data, characterName) {
    clearMesh();
    if (characterName && characterName !== lastLoadedCharacter) {
        lastLoadedCharacter = characterName;
    }

    const positions = new Float32Array(data.positions);
    const normals = new Float32Array(data.normals);
    const uvs = new Float32Array(data.uvs);
    const indices = new Uint32Array(data.indices);
    const skinIndices = new Uint16Array(data.skinIndices);
    const skinWeights = new Float32Array(data.skinWeights);

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    geometry.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
    geometry.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
    geometry.setIndex(new THREE.BufferAttribute(indices, 1));
    geometry.setAttribute('skinIndex', new THREE.BufferAttribute(skinIndices, 4));
    geometry.setAttribute('skinWeight', new THREE.BufferAttribute(skinWeights, 4));

    const threeBones = [];
    const boneInverses = [];

    for (let i = 0; i < data.boneNames.length; i++) {
        const bone = new THREE.Bone();
        bone.name = data.boneNames[i];

        if (data.bindPose) {
            const bp = data.bindPose;
            bone.position.set(bp[i * 7], bp[i * 7 + 1], bp[i * 7 + 2]);
            bone.quaternion.set(bp[i * 7 + 3], bp[i * 7 + 4], bp[i * 7 + 5], bp[i * 7 + 6]);
        }

        threeBones.push(bone);
    }

    for (let i = 0; i < data.boneNames.length; i++) {
        const parentIdx = data.boneParents[i];
        if (parentIdx >= 0 && parentIdx < threeBones.length) {
            threeBones[parentIdx].add(threeBones[i]);
        }
    }

    currentSkeleton = new THREE.Skeleton(threeBones);

    for (let i = 0; i < threeBones.length; i++) {
        boneInverses.push(threeBones[i].matrixWorld.clone().invert());
    }
    currentSkeleton.boneInverses = boneInverses;
    currentSkeleton.update();

    const material = new THREE.MeshPhongMaterial({
        color: 0xcccccc,
        side: THREE.DoubleSide,
        skinning: true
    });

    currentMesh = new THREE.SkinnedMesh(geometry, material);
    currentMesh.add(threeBones[0]);
    currentMesh.bind(currentSkeleton);
    scene.add(currentMesh);

    if (skeletonHelper) scene.remove(skeletonHelper);
    skeletonHelper = new THREE.SkeletonHelper(threeBones[0]);
    skeletonHelper.material.color.set(0x89b4fa);
    skeletonHelper.material.linewidth = 2;
    scene.add(skeletonHelper);
}

function buildSkeleton(data) {
    if (skeletonHelper) {
        scene.remove(skeletonHelper);
        skeletonHelper = null;
    }
    if (rootBone && !currentMesh) {
        scene.remove(rootBone);
        rootBone = null;
    }
    bones = [];
    boneMap = {};

    if (currentMesh) {
        currentMesh.traverse((child) => {
            if (child.isBone) {
                bones.push(child);
                boneMap[child.name] = child;
            }
        });
        rootBone = bones[0];
    } else {
        const boneData = data.skeleton.bones;
        const bindPose = data.skeleton.bindPose;

        for (let i = 0; i < boneData.length; i++) {
            const bone = new THREE.Bone();
            bone.name = boneData[i].name;

            if (bindPose && bindPose[i]) {
                const pos = bindPose[i].position;
                const rot = bindPose[i].rotation;
                bone.position.set(pos.x, pos.y, pos.z);
                bone.quaternion.set(rot.x, rot.y, rot.z, rot.w);
            }

            bones.push(bone);
            boneMap[boneData[i].name] = bone;
            boneMap[i] = bone;
        }

        for (let i = 0; i < boneData.length; i++) {
            const parentIdx = boneData[i].parent;
            if (parentIdx >= 0 && boneMap[parentIdx]) {
                boneMap[parentIdx].add(bones[i]);
            }
        }

        rootBone = bones[0];
        if (!rootBone) return;

        scene.add(rootBone);
    }

    if (!rootBone) return;

    skeletonHelper = new THREE.SkeletonHelper(rootBone);
    skeletonHelper.material.color.set(0x89b4fa);
    skeletonHelper.material.linewidth = 2;
    scene.add(skeletonHelper);

    document.getElementById('info').textContent = `${bones.length} bones`;

    return rootBone;
}

function loadAnimation(data) {
    animationData = data;
    const anim = data.animation;

    document.getElementById('no-data').style.display = 'none';
    document.getElementById('error-overlay').style.display = 'none';

    if (!anim || !anim.tracks || anim.tracks.length === 0) {
        console.warn('No animation tracks found');
        return;
    }

    buildSkeleton(data);

    const tracks = [];

    for (const track of anim.tracks) {
        const boneName = track.boneName;
        const bone = boneMap[boneName];
        if (!bone) continue;

        const times = track.times;
        if (!times || times.length === 0) continue;

        if (track.positions && track.positions.length > 0) {
            const values = new Float32Array(track.positions.length * 3);
            for (let i = 0; i < track.positions.length; i++) {
                values[i * 3] = track.positions[i].x;
                values[i * 3 + 1] = track.positions[i].y;
                values[i * 3 + 2] = track.positions[i].z;
            }
            tracks.push(new THREE.VectorKeyframeTrack(
                `${boneName}.position`, times, values
            ));
        }

        if (track.rotations && track.rotations.length > 0) {
            const values = new Float32Array(track.rotations.length * 4);
            for (let i = 0; i < track.rotations.length; i++) {
                values[i * 4] = track.rotations[i].x;
                values[i * 4 + 1] = track.rotations[i].y;
                values[i * 4 + 2] = track.rotations[i].z;
                values[i * 4 + 3] = track.rotations[i].w;
            }
            tracks.push(new THREE.QuaternionKeyframeTrack(
                `${boneName}.quaternion`, times, values
            ));
        }

        if (track.scales && track.scales.length > 0) {
            const values = new Float32Array(track.scales.length * 3);
            for (let i = 0; i < track.scales.length; i++) {
                values[i * 3] = track.scales[i].x;
                values[i * 3 + 1] = track.scales[i].y;
                values[i * 3 + 2] = track.scales[i].z;
            }
            tracks.push(new THREE.VectorKeyframeTrack(
                `${boneName}.scale`, times, values
            ));
        }
    }

    if (tracks.length === 0) {
        console.warn('No valid tracks created');
        return;
    }

    const clip = new THREE.AnimationClip(
        anim.name || 'animation',
        anim.duration || 1.0,
        tracks
    );

    const target = currentMesh || rootBone;
    mixer = new THREE.AnimationMixer(target);
    currentAction = mixer.clipAction(clip);
    currentAction.setLoop(THREE.LoopRepeat);
    currentAction.play();
    isPlaying = true;

    if (!cameraInitialized) {
        resetCamera();
        cameraInitialized = true;
    }

    clock.start();
    clock.getDelta();

    window.chrome.webview.postMessage(JSON.stringify({
        action: 'animationLoaded',
        name: anim.name,
        duration: anim.duration,
        numFrames: anim.numFrames,
        fps: anim.fps,
        trackCount: tracks.length
    }));
}

function setPlaybackSpeed(speed) {
    playbackSpeed = speed;
    if (currentAction) {
        currentAction.setPlaybackRate(speed);
    }
}

function play() {
    if (currentAction) {
        currentAction.play();
        isPlaying = true;
        clock.start();
        clock.getDelta();
    }
}

function pause() {
    if (currentAction) {
        currentAction.paused = true;
        isPlaying = false;
    }
}

function stop() {
    if (currentAction) {
        currentAction.stop();
        isPlaying = false;
    }
}

function seekTo(progress) {
    if (currentAction && currentAction.getClip()) {
        const duration = currentAction.getClip().duration;
        currentAction.time = progress * duration;
        if (mixer) mixer.update(0);
    }
}

function resetCamera() {
    const box = new THREE.Box3();

    if (currentMesh && currentMesh.geometry) {
        currentMesh.geometry.computeBoundingBox();
        box.copy(currentMesh.geometry.boundingBox);
        currentMesh.updateMatrixWorld(true);
        box.applyMatrix4(currentMesh.matrixWorld);
    } else if (bones.length > 0) {
        for (let i = 0; i < bones.length; i++) {
            bones[i].updateMatrixWorld(true);
            const pos = new THREE.Vector3();
            pos.setFromMatrixPosition(bones[i].matrixWorld);
            box.expandByPoint(pos);
        }
    } else {
        return;
    }

    const center = new THREE.Vector3();
    box.getCenter(center);

    const size = new THREE.Vector3();
    box.getSize(size);
    const maxDim = Math.max(size.x, size.y, size.z);
    const fov = camera.fov * (Math.PI / 180);
    let distance = (maxDim / 2) / Math.tan(fov / 2);
    distance = Math.max(distance, 1.0);
    distance *= 1.2;

    orbitTarget.copy(center);
    orbitSpherical.radius = distance;
    orbitSpherical.phi = Math.PI / 3.5;
    orbitSpherical.theta = Math.PI / 4;

    if (updateOrbitCamera) updateOrbitCamera();
}

function showError(message) {
    if (currentAction) {
        currentAction.stop();
        currentAction = null;
    }
    if (mixer) {
        mixer.stopAllAction();
        mixer = null;
    }
    isPlaying = false;

    if (skeletonHelper) {
        scene.remove(skeletonHelper);
        skeletonHelper = null;
    }
    if (rootBone) {
        scene.remove(rootBone);
        rootBone = null;
    }
    bones = [];
    boneMap = {};
    animationData = null;

    document.getElementById('error-text').textContent = message;
    document.getElementById('error-overlay').style.display = '';
    document.getElementById('no-data').style.display = 'none';

    window.chrome.webview.postMessage(JSON.stringify({ action: 'animationError' }));
}

window.chrome.webview.addEventListener('message', event => {
    const data = event.data;
    if (typeof data === 'string') {
        try {
            const parsed = JSON.parse(data);
            if (parsed.action === 'loadAnimation') {
                loadAnimation(parsed.data);
            } else if (parsed.action === 'setSpeed') {
                setPlaybackSpeed(parsed.speed);
            } else if (parsed.action === 'play') {
                play();
            } else if (parsed.action === 'pause') {
                pause();
            } else if (parsed.action === 'stop') {
                stop();
            } else if (parsed.action === 'seek') {
                seekTo(parsed.progress);
            }
        } catch (e) {
            console.error('Message parse error:', e);
        }
    }
});

window.addEventListener('load', init);
