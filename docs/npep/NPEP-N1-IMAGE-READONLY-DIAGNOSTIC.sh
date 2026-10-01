#!/usr/bin/env bash
(
  set -eu
  image=''
  for candidate in \
    sha256:c0e7aac95c947ef8badc386e5e4778a52cb7d46555799b79fa6c58e01fa21b8d \
    sha256:e3227d12831303a2c2d9a433a776623ecd77973197b749a5818af4b0109a5404 \
    sha256:430e84cf83f3d2fc04a302834a4e721f4988267062bc31bafa18dc39fe2cfde4
  do
    if docker image inspect "$candidate" >/dev/null 2>&1; then
      image="$candidate"
      break
    fi
  done
  if [ -z "$image" ]; then
    echo '未找到保留的新镜像；停止检查，请回报这一行。'
    exit 1
  fi
  docker image inspect --format '检查镜像={{.Id}} user={{.Config.User}}' "$image"
  docker run --rm --pull=never --network none --read-only \
    --cap-drop ALL --security-opt no-new-privileges --user 0 \
    --entrypoint node "$image" -e '
      const fs = require("node:fs");
      for (const p of ["/app", "/app/package.json", "/app/scripts", "/app/scripts/npep-config.js", "/app/prisma"]) {
        try {
          const s = fs.statSync(p);
          console.log(JSON.stringify({path:p,uid:s.uid,gid:s.gid,mode:(s.mode & 511).toString(8)}));
        } catch(e) { console.log(JSON.stringify({path:p,error:e.code})); }
      }
    '
  result=0
  docker run --rm --pull=never --network none --read-only \
    --cap-drop ALL --security-opt no-new-privileges --user node \
    --workdir /app \
    --env NPEP_ENABLED=false \
    --env NPEP_DEPLOYMENT_FILE=/var/lib/npclassworks-npep/deployment.json \
    --entrypoint node "$image" scripts/npep-config.js check || result=$?
  printf '配置检查退出码=%s\n' "$result"
)

