import os
import subprocess
import sys
import time
import threading

import paramiko


PROJECT_ROOT = r"E:\作品\AI桌宠开放平台"
INCLUDES = ["server", "server_pets", "data", "requirements-server.txt", "requirements.txt"]
REMOTE_ROOT = "/root/ai-pet-platform"
LOG_PATH = r"E:\ATRI\deploy_ai_pet_platform.log"


def log(message):
    stamp = time.strftime("%Y-%m-%d %H:%M:%S")
    with open(LOG_PATH, "a", encoding="utf-8") as f:
        f.write(f"[{stamp}] {message}\n")
        f.flush()


def payload_size():
    total = 0
    files = 0
    ignored_parts = {".git", ".agents", "__pycache__", "users_temp"}
    for rel in INCLUDES:
        path = os.path.join(PROJECT_ROOT, rel)
        if os.path.isfile(path):
            total += os.path.getsize(path)
            files += 1
            continue
        for root, dirs, names in os.walk(path):
            dirs[:] = [d for d in dirs if d not in ignored_parts]
            for name in names:
                fp = os.path.join(root, name)
                try:
                    total += os.path.getsize(fp)
                    files += 1
                except OSError:
                    pass
    return files, total


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

    files, total = payload_size()
    log(f"starting deploy upload: {PROJECT_ROOT} -> {REMOTE_ROOT}")
    log(f"payload: {files} files, {total / (1024 ** 2):.2f} MiB")

    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(
        host,
        port=port,
        username=user,
        password=password,
        timeout=30,
        banner_timeout=30,
        auth_timeout=30,
    )

    tar_args = [
        "tar",
        "-C",
        PROJECT_ROOT,
        "--exclude=.git",
        "--exclude=.agents",
        "--exclude=__pycache__",
        "--exclude=users_temp",
        "-czf",
        "-",
        *INCLUDES,
    ]
    tar_proc = subprocess.Popen(
        tar_args,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    threading.Thread(target=pipe_stderr, args=(tar_proc,), daemon=True).start()

    prep_cmd = (
        f"set -e; "
        f"if [ -d {REMOTE_ROOT} ]; then mv {REMOTE_ROOT} {REMOTE_ROOT}.bak.$(date +%Y%m%d%H%M%S); fi; "
        f"mkdir -p {REMOTE_ROOT}"
    )
    stdin, stdout, stderr = client.exec_command(prep_cmd)
    prep_rc = stdout.channel.recv_exit_status()
    prep_err = stderr.read().decode("utf-8", "replace").strip()
    if prep_rc != 0:
        log(f"FAILED: remote prep exited {prep_rc}: {prep_err}")
        raise SystemExit(prep_rc)

    channel = client.get_transport().open_session()
    channel.exec_command(f"tar -xzpf - -C {REMOTE_ROOT}")

    sent = 0
    started = time.time()
    last_log = started
    try:
        while True:
            chunk = tar_proc.stdout.read(1024 * 1024)
            if not chunk:
                break
            channel.sendall(chunk)
            sent += len(chunk)
            now = time.time()
            if now - last_log >= 15:
                elapsed = max(now - started, 0.001)
                log(f"progress: {sent / (1024 ** 2):.1f} MiB compressed stream, {(sent / (1024 ** 2)) / elapsed:.1f} MiB/s")
                last_log = now
    finally:
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

    elapsed = max(time.time() - started, 0.001)
    log(f"completed deploy upload in {elapsed:.1f}s, streamed {sent / (1024 ** 2):.1f} MiB")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        log(f"FAILED: {type(exc).__name__}: {exc}")
        raise
