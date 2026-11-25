FROM python:3.12-slim
WORKDIR /app
COPY git_lock_server.py .
RUN mkdir /data && chown 10001:10001 /data
USER 10001:10001
EXPOSE 8765
ENTRYPOINT ["python", "-u", "/app/git_lock_server.py", "--host", "0.0.0.0", "--db", "/data/locks.sqlite3", "--config", "/config/users.json"]
