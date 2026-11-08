FROM python:3.12-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /srv
COPY server-python/requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt
COPY server-python/app.py ./
RUN useradd --system --uid 11001 --home /srv hf811 && mkdir -p /srv/data && chown -R hf811:hf811 /srv
USER hf811
EXPOSE 8090
CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8090", "--proxy-headers", "--forwarded-allow-ips", "127.0.0.1"]
