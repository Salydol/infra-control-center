FROM python:3.12-slim
WORKDIR /app
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
COPY pyproject.toml ./
COPY src/ src/
RUN pip install --no-cache-dir .
RUN useradd --system --uid 10001 icc
USER icc
EXPOSE 8000
CMD ["uvicorn", "icc_ai.main:app", "--host", "0.0.0.0", "--port", "8000"]
