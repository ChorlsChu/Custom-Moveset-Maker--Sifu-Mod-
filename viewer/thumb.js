// Offscreen bind-pose mesh thumbnail renderer for the model picker cards.

let scene, camera, renderer;
let currentMesh = null;

function init() {
    scene = new THREE.Scene();
    scene.background = new THREE.Color(0x313244);

    camera = new THREE.PerspectiveCamera(45, 1, 0.1, 10000);
    camera.position.set(0, 1.2, 3);
    camera.lookAt(0, 0.8, 0);

    renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
    renderer.setPixelRatio(1);
    renderer.setSize(128, 128);
    document.body.appendChild(renderer.domElement);

    scene.add(new THREE.AmbientLight(0xffffff, 0.95));

    const dir = new THREE.DirectionalLight(0xffffff, 0.9);
    dir.position.set(5, 10, 5);
    scene.add(dir);

    const back = new THREE.DirectionalLight(0xffffff, 0.35);
    back.position.set(-5, 6, -5);
    scene.add(back);
}

function disposeMesh() {
    if (!currentMesh) return;
    scene.remove(currentMesh);
    currentMesh.geometry.dispose();
    const mats = Array.isArray(currentMesh.material) ? currentMesh.material : [currentMesh.material];
    const seenTextures = new Set();
    for (const m of mats) {
        if (!m) continue;
        if (m.map && !seenTextures.has(m.map)) {
            seenTextures.add(m.map);
            m.map.dispose();
        }
        m.dispose();
    }
    currentMesh = null;
}

function frameCamera(box) {
    const center = new THREE.Vector3();
    box.getCenter(center);
    const size = new THREE.Vector3();
    box.getSize(size);
    const maxDim = Math.max(size.x, size.y, size.z);
    const fov = camera.fov * (Math.PI / 180);
    let distance = (maxDim / 2) / Math.tan(fov / 2);
    distance = Math.max(distance, 1.0) * 1.15;

    const theta = Math.PI / 4;
    const phi = Math.PI / 3;
    camera.position.set(
        center.x + distance * Math.sin(phi) * Math.sin(theta),
        center.y + distance * Math.cos(phi),
        center.z + distance * Math.sin(phi) * Math.cos(theta)
    );
    camera.lookAt(center);
}

function loadTexture(url) {
    return new Promise((resolve) => {
        if (!url) return resolve(null);
        new THREE.TextureLoader().load(
            url,
            (tex) => resolve(tex),
            undefined,
            () => resolve(null)
        );
    });
}

// Gap-fills section groups so material indices cover the whole index buffer
// (ungrouped triangles are skipped whenever a material array is used).
function coverGroups(groups, totalIndices) {
    const sorted = groups
        .filter((g) => g && g.count > 0)
        .slice()
        .sort((a, b) => a.start - b.start);
    const final = [];
    let pos = 0;
    for (const g of sorted) {
        const start = Math.max(g.start, pos);
        const end = g.start + g.count;
        if (end <= pos) continue;
        if (start > pos) final.push({ png: null, start: pos, count: start - pos });
        final.push({ png: g.png, start: start, count: end - start });
        pos = end;
    }
    if (pos < totalIndices) final.push({ png: null, start: pos, count: totalIndices - pos });
    return final;
}

async function renderThumb(id, data, size) {
    try {
        disposeMesh();

        const px = (typeof size === 'number' && size > 0) ? Math.min(size, 1024) : 128;
        if (renderer.domElement.width !== px || renderer.domElement.height !== px)
            renderer.setSize(px, px, false);

        const positions = new Float32Array(data.positions);
        const normals = new Float32Array(data.normals);
        const uvs = new Float32Array(data.uvs);
        const indices = new Uint32Array(data.indices);

        const geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geometry.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
        if (data.uvs && data.uvs.length === positions.length / 3 * 2)
            geometry.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
        geometry.setIndex(new THREE.BufferAttribute(indices, 1));
        geometry.computeBoundingBox();

        let material;
        if (Array.isArray(data.materials) && data.materials.length > 0) {
            const groups = coverGroups(data.materials, indices.length);
            const textureUrls = [...new Set(groups.map((g) => g.png || ''))];
            const textures = new Map();
            await Promise.all(textureUrls.map(async (u) => {
                textures.set(u, await loadTexture(u));
            }));

            material = [];
            groups.forEach((g, i) => {
                geometry.addGroup(g.start, g.count, i);
                const tex = g.png ? textures.get(g.png) : null;
                if (tex) {
                    material.push(new THREE.MeshPhongMaterial({
                        map: tex,
                        side: THREE.DoubleSide
                    }));
                } else {
                    material.push(new THREE.MeshPhongMaterial({
                        color: 0x9aa5ce,
                        side: THREE.DoubleSide
                    }));
                }
            });
        } else {
            material = new THREE.MeshPhongMaterial({
                color: 0x9aa5ce,
                side: THREE.DoubleSide
            });
        }

        currentMesh = new THREE.Mesh(geometry, material);
        scene.add(currentMesh);

        frameCamera(geometry.boundingBox);
        renderer.render(scene, camera);

        const dataUrl = renderer.domElement.toDataURL('image/png');
        disposeMesh();

        window.chrome.webview.postMessage({ type: 'thumb', id: id, data: dataUrl });
    } catch (e) {
        disposeMesh();
        window.chrome.webview.postMessage({ type: 'thumb', id: id, error: String(e) });
    }
}

window.renderThumb = renderThumb;
init();
window.chrome.webview.postMessage({ type: 'thumbReady' });
