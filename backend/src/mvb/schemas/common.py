from __future__ import annotations

from typing import Generic, TypeVar

from pydantic import BaseModel, ConfigDict, Field

T = TypeVar("T")


class ORMModel(BaseModel):
    model_config = ConfigDict(from_attributes=True)


class ErrorBody(BaseModel):
    code: str
    message: str
    detail: dict = Field(default_factory=dict)


class ErrorEnvelope(BaseModel):
    error: ErrorBody
    request_id: str


class Page(BaseModel, Generic[T]):
    items: list[T]
    total: int
    limit: int
    offset: int


class OkResponse(BaseModel):
    ok: bool = True
    detail: dict = Field(default_factory=dict)
