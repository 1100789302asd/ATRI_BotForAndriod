import os
import subprocess
import sys
import time
import threading

import paramiko


PROJECT_ROOT = r"E:\作品\AI桌宠开放平台"
REMOTE_ROOT = "/root/ai-pet-platform"
LOG_PATH = r"E:\ATRI\redeploy_ai_pet_server.log"


def log(message):
    stamp = time.strftime("%Y-%m-%d %H:%M:%S")
    with open(LOG_PATH, "a", encoding="utf-8") as f:
        f.write(f"[{stamp}] {message}\n")
        f.flush()


def pipe_stderr(proc):
    for raw in iter(proc.stderr.readline, b""):
        text = raw.decode("utf-8", "replace").strip()
        if text:
            log(f"local tar: {text}")


def main():
    host = os.environ["SSH_HOST"]
    port = int(os.environ["SSH_PORT"])
    user = os.environ["SSH_USER"]
    password = os.environ["SSH_PASS"]

    log(f"starting server redeploy: {PROJECT_ROOT}\\server -> {REMOTE_ROOT}/server")
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(host, port=port, username=user, password=password, timeout=30)

    prep_cmd = (
        f"set -e; mkdir -p {REMOTE_ROOT}; "
        f"if [ -d {REMOTE_ROOT}/server ]; then "
        f"mv {REMOTE_ROOT}/server {REMOTE_ROOT}/server.bak.$(date +%Y%m%d%H%M%S); "
        f"fi"
    )
    stdin, stdout, stderr = client.exec_command(prep_cmd)
    rc = stdout.channel.recv_exit_status()
    err = stderr.read().decode("utf-8", "replace").strip()
    if rc != 0:
        log(f"FAILED: remote prep exited {rc}: {err}")
        raise SystemExit(rc)

    tar_proc = subprocess.Popen(
        [
            "tar",
            "-C",
            PROJECT_ROOT,
            "--exclude=.git",
            "--exclude=.agents",
            "--exclude=__pycache__",
            "-czf",
            "-",
            "server",
            "requirements-server.txt",
            "requirements.txt",
        ],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    threading.Thread(target=pipe_stderr, args=(tar_proc,), daemon=True).start()

    channel = client.get_transport().open_session()
    channel.exec_command(f"tar -xzpf - -C {REMOTE_ROOT}")
    sent = 0
    started = time.time()
    while True:
        chunk = tar_proc.stdout.read(1024 * 1024)
        if not chunk:
            break
        channel.sendall(chunk)
        sent += len(chunk)
    try:
        channel.shutdown_write()
    except Exception:
        pass

    local_rc = tar_proc.wait()
    remote_rc = channel.recv_exit_status()
    remote_err = channel.makefile_stderr("rb").read().decode("utf-8", "replace").strip()
    client.close()

    if local_rc != 0:
        log(f"FAILED: local tar exited {local_rc}")
        raise SystemExit(local_rc)
    if remote_rc != 0:
        log(f"FAILED: remote tar exited {remote_rc}: {remote_err}")
        raise SystemExit(remote_rc)
    log(f"completed server redeploy in {time.time() - started:.1f}s, streamed {sent / (1024 ** 2):.2f} MiB")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        log(f"FAILED: {type(exc).__name__}: {exc}")
        raise
