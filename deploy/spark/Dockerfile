FROM nvcr.io/nvidia/pytorch:26.02-py3

ARG PIP_INDEX_URL=https://pypi.tuna.tsinghua.edu.cn/simple
ENV PIP_INDEX_URL=${PIP_INDEX_URL} \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    PYTHONUNBUFFERED=1 \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1

# Qwen3-ASR 0.0.6 requires Transformers 4.57.6. Keep this service isolated from
# the Spark's newer vLLM image, whose Transformers 5.x API is not compatible.
RUN python -m pip install --no-cache-dir \
    "qwen-asr==0.0.6" \
    "flask==3.1.2" \
    "gunicorn==23.0.0"

WORKDIR /opt/voicescreen
COPY voicescreen_model_service.py /opt/voicescreen/voicescreen_model_service.py

EXPOSE 18765
ENTRYPOINT ["gunicorn", "--workers", "1", "--threads", "4", "--timeout", "300", "--graceful-timeout", "60", "--bind", "0.0.0.0:18765", "voicescreen_model_service:create_app()"]
