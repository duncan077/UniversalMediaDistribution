# The oldest supported glibc baseline. Runtime libraries are rebuilt from the
# distribution's security updates; package versions are recorded in the bundle.
ARG VERIFY_IMAGE=debian:bookworm-slim
FROM debian:bookworm-slim AS bundle
RUN apt-get update && apt-get install -y --no-install-recommends \
    libvlc5 vlc-plugin-base vlc-plugin-video-output python3 patchelf \
    && rm -rf /var/lib/apt/lists/*
COPY bundle-vlc.py /build/bundle-vlc.py
RUN python3 /build/bundle-vlc.py /payload

FROM bundle AS samples
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg \
    && ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc2=size=64x64:rate=10 \
       -f lavfi -i sine=frequency=440:sample_rate=8000 -t 1 \
       -c:v libx264 -pix_fmt yuv420p -c:a aac /sample.mp4

# No VLC is installed in this stage. Tests load only the bundled runtime.
FROM ${VERIFY_IMAGE} AS verify
RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 libfontconfig1 libice6 libsm6 libx11-6 \
    libwebkit2gtk-4.1-0 gstreamer1.0-libav gstreamer1.0-plugins-good xvfb xauth dbus-x11 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=bundle /payload /bundle
COPY README-Linux.md /bundle/README-Linux.md
COPY --from=samples /sample.mp4 /sample.mp4
COPY verify-vlc.py /verify-vlc.py
COPY package-desktop.py /package-desktop.py
COPY smoke-desktop.sh /smoke-desktop.sh
RUN python3 /verify-vlc.py /bundle /sample.mp4
