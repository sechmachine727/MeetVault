#!/usr/bin/env sh
# Build the MeetVault backend deployment image, and optionally export it as a portable tarball.
#
#   ./build.sh                          # default image (mock engines), tag "local"
#   ./build.sh --with-transcribe        # + faster-whisper (real ASR)
#   ./build.sh --with-diarize           # + pyannote/torch (real speaker turns)
#   ./build.sh --tag 1.0.0 --save       # tag it and write dist/meetvault-backend-1.0.0.tar.gz
#   ./build.sh --save --out /srv/ship   # export somewhere else
#
# A built image is ~1 GB (1.6 GB with ASR, more with diarization), which is why the image itself
# is NOT in the repository: this script is the reproducible way to produce it, and --save gives
# you one file to move to a server that cannot build (see docs/DEPLOY.md).
#
# The export is a `docker save` of the local image. On the target machine:
#   gunzip -c meetvault-backend-1.0.0.tar.gz | docker load
set -eu

TAG="local"
SAVE=0
OUT="dist"
WITH_TRANSCRIBE="false"
WITH_DIARIZE="false"
APP_EXTRAS="prod"

usage() {
    sed -n '2,18p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

while [ $# -gt 0 ]; do
    case "$1" in
        --tag) TAG="$2"; shift 2 ;;
        --save) SAVE=1; shift ;;
        --out) OUT="$2"; shift 2 ;;
        --with-transcribe) WITH_TRANSCRIBE="true"; shift ;;
        --with-diarize) WITH_DIARIZE="true"; shift ;;
        --extras) APP_EXTRAS="$2"; shift 2 ;;
        -h|--help) usage 0 ;;
        *) echo "unknown option: $1" >&2; usage 2 ;;
    esac
done

# Resolve the repository root from this script's location, so the build context is right no
# matter which directory the script is invoked from.
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
REPO_ROOT=$(CDPATH= cd -- "$SCRIPT_DIR/.." && pwd)

IMAGE="meetvault-backend:$TAG"
echo "== repo root: $REPO_ROOT"
echo "== image:     $IMAGE"
echo "== extras:    $APP_EXTRAS (transcribe=$WITH_TRANSCRIBE diarize=$WITH_DIARIZE)"

docker build \
    --file "$SCRIPT_DIR/Dockerfile" \
    --build-arg "APP_EXTRAS=$APP_EXTRAS" \
    --build-arg "WITH_TRANSCRIBE=$WITH_TRANSCRIBE" \
    --build-arg "WITH_DIARIZE=$WITH_DIARIZE" \
    --tag "$IMAGE" \
    "$REPO_ROOT"

# The digest is the thing to quote when you need to say exactly which image was built: a tag
# is movable, the image ID is not.
IMAGE_ID=$(docker image inspect --format '{{.Id}}' "$IMAGE")
SIZE=$(docker image inspect --format '{{.Size}}' "$IMAGE" | awk '{printf "%.0f", $1/1048576}')
echo "== built $IMAGE  id=$IMAGE_ID  size=${SIZE} MB"

if [ "$SAVE" = "1" ]; then
    mkdir -p "$OUT"
    ARCHIVE="$OUT/meetvault-backend-$TAG.tar.gz"
    echo "== exporting to $ARCHIVE (a few minutes; this is a ~1 GB image)"
    docker save "$IMAGE" | gzip -1 > "$ARCHIVE"
    BYTES=$(wc -c < "$ARCHIVE" | tr -d ' ')
    # sha256sum prefixes the name with "\" on Windows, so take the first field only.
    SHA=$(sha256sum "$ARCHIVE" | awk '{print $1}')
    echo "== wrote $ARCHIVE (${BYTES} bytes)"
    echo "== sha256: $SHA"
    echo "== image id: $IMAGE_ID"
    echo "== load it on the target: gunzip -c $(basename "$ARCHIVE") | docker load"
fi
