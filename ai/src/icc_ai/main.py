from fastapi import FastAPI

from icc_ai import __version__

app = FastAPI(title="ICC AI", version=__version__)


@app.get("/healthz")
def healthz() -> dict[str, str]:
    return {"status": "ok"}


@app.get("/api/version")
def version() -> dict[str, str]:
    return {"name": "icc-ai", "version": __version__}
