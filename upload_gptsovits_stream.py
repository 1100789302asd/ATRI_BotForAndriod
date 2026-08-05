import os
import subprocess
import sys
import time
import threading

import paramiko


LOCAL_DIR = r"E:\v2\GPT-SoVITS-v2-240821"
REMOTE_PARENT = "/root"
REMOTE_DIR = "/root/GPT-SoVITS-v2-240821"
LOG_PATH = r"E:\ATRI\upload_gptsovits_stream.log"


def log(message):
    stamp = time.strftime("%Y-%m-%d %H:%M:%S")
    with open(LOG_PATH, "a", encoding="utf-8") as f:
        f.write(f"[{stamp}] {message}\n")
        f.flush()


def dir_size(path):
    total = 0
    count = 0
    for root, _, files in os.walk(path):
        for name in files:
            fp = os.path.join(root, name)
            try:
                total += os.path.getsize(fp)
                count += 1
            except OSError:
                pass
    return total, count


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

    if not os.path.isdir(LOCAL_DIR):
        raise SystemExit(f"missing local dir: {LOCAL_DIR}")

    total, count = dir_size(LOCAL_DIR)
    log(f"starting upload: {LOCAL_DIR} -> {REMOTE_DIR}")
    log(f"local payload: {count} files, {total / (1024 ** 3):.2f} GiB")

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

    parent = os.path.dirname(LOCAL_DIR)
    base = os.path.basename(LOCAL_DIR)
    tar_proc = subprocess.Popen(
        ["tar", "-C", parent, "-czf", "-", base],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    threading.Thread(target=pipe_stderr, args=(tar_proc,), daemon=True).start()

    transport = client.get_transport()
    channel = transport.open_session()
    channel.exec_command(f"mkdir -p {REMOTE_PARENT!r} && tar -xzpf - -C {REMOTE_PARENT!r}")

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
            if now - last_log >= 30:
                elapsed = max(now - started, 0.001)
                mib_sent = sent / (1024 ** 2)
                speed = mib_sent / elapsed
                log(f"progress: {mib_sent:.0f} MiB compressed stream, {speed:.1f} MiB/s")
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
    log(f"completed upload in {elapsed / 60:.1f} minutes, streamed {sent / (1024 ** 3):.2f} GiB")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        log(f"FAILED: {type(exc).__name__}: {exc}")
        raise
