const fs = require('fs');
const path = require('path');
const net = require('net');
const { spawn, execSync } = require('child_process');

const PROJECT_ROOT = path.resolve(__dirname, '../..');
const BUILD_EXE = path.join(PROJECT_ROOT, 'build', 'filelist.exe');
const TEMP_DIR = path.join(PROJECT_ROOT, 'temp', 'e2e');

function getFreePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.listen(0, '127.0.0.1', () => {
      const port = s.address().port;
      s.close(() => resolve(port));
    });
  });
}

function ensureBinary() {
  let needBuild = !fs.existsSync(BUILD_EXE);
  if (!needBuild) {
    const binMtime = fs.statSync(BUILD_EXE).mtimeMs;
    const checkDir = (dir) => {
      const entries = fs.readdirSync(dir, { withFileTypes: true });
      for (const ent of entries) {
        const full = path.join(dir, ent.name);
        if (ent.isDirectory()) {
          checkDir(full);
        } else if (ent.isFile()) {
          if (fs.statSync(full).mtimeMs > binMtime) {
            needBuild = true;
            return;
          }
        }
      }
    };
    checkDir(path.join(PROJECT_ROOT, 'src'));
  }

  if (needBuild) {
    console.log('[E2E] Building latest filelist.exe...');
    execSync('go build -o ../build/filelist.exe .', {
      cwd: path.join(PROJECT_ROOT, 'src'),
      stdio: 'inherit'
    });
  }
}

function setupFixtures(testDir) {
  const root1 = path.join(testDir, 'root1');
  const root2 = path.join(testDir, 'root2');

  // Clean & create directories
  fs.rmSync(testDir, { recursive: true, force: true });
  fs.mkdirSync(path.join(root1, 'subfolder', 'deep'), { recursive: true });
  fs.mkdirSync(path.join(root1, 'empty-dir'), { recursive: true });
  fs.mkdirSync(root2, { recursive: true });

  // Root1 files
  fs.writeFileSync(path.join(root1, 'hello.txt'), 'Hello FileList E2E Content\n', 'utf-8');
  fs.writeFileSync(path.join(root1, 'apple.txt'), 'A file starting with A\n', 'utf-8');
  fs.writeFileSync(path.join(root1, 'zebra.txt'), 'Z file for sorting test\n', 'utf-8');
  fs.writeFileSync(path.join(root1, '.env'), 'SECRET_KEY=123456\n', 'utf-8'); // excluded sensitive file
  fs.writeFileSync(path.join(root1, 'subfolder', 'nested.txt'), 'Nested content\n', 'utf-8');
  fs.writeFileSync(path.join(root1, 'subfolder', 'deep', 'deep.log'), 'Deep log content\n', 'utf-8');

  // Root1 images folder
  const imgDir = path.join(root1, 'images');
  fs.mkdirSync(imgDir, { recursive: true });
  const sampleSrcDir = 'C:\\Home\\Temp\\Jigsaw_Organized\\Animals';
  if (fs.existsSync(sampleSrcDir)) {
    const files = fs.readdirSync(sampleSrcDir).filter(f => f.endsWith('.png')).slice(0, 4);
    for (const f of files) {
      fs.copyFileSync(path.join(sampleSrcDir, f), path.join(imgDir, f));
    }
  } else {
    fs.writeFileSync(path.join(imgDir, 'sample.png'), Buffer.from('89504e470d0a1a0a0000000d49484452000000010000000108060000001f15c4890000000a49444154789c63000100000500010d0a2db40000000049454e44ae426082', 'hex'));
  }

  // Root1 media folder
  const mediaDir = path.join(root1, 'media');
  fs.mkdirSync(mediaDir, { recursive: true });
  fs.writeFileSync(path.join(mediaDir, 'sample.mp4'), Buffer.from('000000186674797069736f6d0000020069736f6d69736f32617663310000000866726565', 'hex'));
  fs.writeFileSync(path.join(mediaDir, 'sample.mp3'), Buffer.from('49443303000000000000', 'hex'));

  // Root2 files
  fs.writeFileSync(path.join(root2, 'guide.md'), '# FileList Guide\n', 'utf-8');

  return { root1, root2 };
}

async function waitForServer(url, token = '', timeoutMs = 8000) {
  const startTime = Date.now();
  const headers = token ? { Authorization: `Bearer ${token}` } : {};
  while (Date.now() - startTime < timeoutMs) {
    try {
      const res = await fetch(`${url}/api/stats`, { headers });
      if (res.ok || res.status === 401) {
        return true;
      }
    } catch (_) {
      // Ignore connection errors during spin-up
    }
    await new Promise((r) => setTimeout(r, 100));
  }
  throw new Error(`Server at ${url} failed to start within ${timeoutMs}ms`);
}

async function startServer(options = {}) {
  ensureBinary();

  const port = options.port || (await getFreePort());
  const instanceName = options.name || 'default';
  const instanceDir = path.join(TEMP_DIR, instanceName);
  const dataDir = path.join(instanceDir, 'data');
  const fixtures = setupFixtures(path.join(instanceDir, 'fixtures'));

  fs.mkdirSync(dataDir, { recursive: true });

  const configPath = path.join(instanceDir, 'config.yaml');
  const configContent = `
server:
  host: "127.0.0.1"
  port: ${port}
  basePath: "${options.basePath || ''}"
  token: "${options.token || ''}"
log:
  level: "debug"
dataDir: "${dataDir.replace(/\\/g, '/')}"
upload:
  enabled: ${options.uploadEnabled !== false}
manage:
  enabled: ${options.manageEnabled !== undefined ? options.manageEnabled : true}
  allowEdit: ${options.allowEdit !== undefined ? options.allowEdit : true}
  allowMkdir: ${options.allowMkdir !== undefined ? options.allowMkdir : true}
  allowRename: ${options.allowRename !== undefined ? options.allowRename : true}
  allowDelete: ${options.allowDelete !== undefined ? options.allowDelete : true}
  deleteToken: "${options.deleteToken || 'e2e-secret-delete-token'}"
index:
  interval: "1h"
  excludeFiles:
    - ".env"
    - "*.key"
roots:
  - url: "/data"
    path: "${fixtures.root1.replace(/\\/g, '/')}"
  - url: "/docs"
    path: "${fixtures.root2.replace(/\\/g, '/')}"
${options.ops ? `ops:
  enabled: ${options.ops.enabled !== false}
  token: "${options.ops.token || ''}"
  terminalToken: "${options.ops.terminalToken || ''}"
${options.ops.blacklist ? '  blacklist:\n' + options.ops.blacklist.map((k) => `    - ${JSON.stringify(k)}`).join('\n') : ''}` : ''}
`;
  fs.writeFileSync(configPath, configContent.trim(), 'utf-8');

  const serverProc = spawn(BUILD_EXE, ['-config', configPath], {
    cwd: PROJECT_ROOT,
    stdio: ['ignore', 'pipe', 'pipe']
  });

  serverProc.stdout.on('data', (d) => {
    if (process.env.DEBUG_SERVER) process.stdout.write(`[SERVER:${port}] ${d}`);
  });
  serverProc.stderr.on('data', (d) => {
    if (process.env.DEBUG_SERVER) process.stderr.write(`[SERVER:${port}] ${d}`);
  });

  const baseUrl = `http://127.0.0.1:${port}${options.basePath || ''}`;
  try {
    await waitForServer(baseUrl, options.token);
  } catch (err) {
    serverProc.kill();
    throw err;
  }

  return {
    port,
    baseUrl,
    instanceDir,
    fixtures,
    configPath,
    process: serverProc,
    stop: async () => {
      return new Promise((resolve) => {
        if (!serverProc || serverProc.killed) {
          resolve();
          return;
        }
        serverProc.on('exit', () => resolve());
        serverProc.kill('SIGTERM');
        setTimeout(() => {
          try {
            serverProc.kill('SIGKILL');
          } catch (_) {}
          resolve();
        }, 1000);
      });
    },
    cleanup: () => {
      try {
        fs.rmSync(instanceDir, { recursive: true, force: true });
      } catch (_) {}
    }
  };
}

module.exports = {
  startServer,
  getFreePort,
  setupFixtures
};
