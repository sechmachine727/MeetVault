from __future__ import annotations

from sqlalchemy import select

from mvb.db.models import MediaAsset, Meeting
from mvb.jobs.handlers import JobContext, handler
from mvb.media.probe import probe_media
from mvb.staleness import mark_all_stale


@handler("media_probe")
def media_probe_job(context: JobContext) -> dict:
    """Probe every media asset of the meeting; set the meeting duration from the audio."""
    meeting = context.session.get(Meeting, context.job.meeting_id)
    if meeting is None:
        raise RuntimeError("meeting not found")
    assets = context.session.scalars(select(MediaAsset).where(MediaAsset.meeting_id == meeting.id)).all()
    for index, asset in enumerate(assets):
        info = probe_media(context.storage.path_for(asset.storage_key))
        asset.probe_json = {
            "error": info.error,
            "codec": info.codec,
            "has_audio": info.has_audio,
            "has_video": info.has_video,
        }
        if info.duration_ms and not asset.duration_ms:
            asset.duration_ms = info.duration_ms
        asset.sample_rate = info.sample_rate
        asset.channels = info.channels
        context.progress((index + 1) / max(1, len(assets)) * 0.9)
    audio = next((asset for asset in assets if asset.kind == "audio"), None)
    if audio is not None and audio.duration_ms:
        meeting.duration_ms = audio.duration_ms
    context.session.flush()
    mark_all_stale(context.session, meeting.id)
    context.progress(1.0)
    return {"assets": len(assets), "duration_ms": meeting.duration_ms, "is_mock": False}
