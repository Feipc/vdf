# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["VDF.Core/VDF.Core.csproj", "VDF.Core/"]
COPY ["VDF.Web/VDF.Web.csproj", "VDF.Web/"]
COPY ["VDF.TestSupport/VDF.TestSupport.csproj", "VDF.TestSupport/"]
COPY ["VDF.Benchmarks/VDF.Benchmarks.csproj", "VDF.Benchmarks/"]
RUN dotnet restore "VDF.Web/VDF.Web.csproj"
RUN dotnet restore "VDF.Benchmarks/VDF.Benchmarks.csproj"
COPY VDF.Core/ VDF.Core/
COPY VDF.Web/ VDF.Web/
COPY VDF.TestSupport/ VDF.TestSupport/
COPY VDF.Benchmarks/ VDF.Benchmarks/
RUN dotnet publish "VDF.Web/VDF.Web.csproj" -c Release -o /app/publish
RUN dotnet publish "VDF.Benchmarks/VDF.Benchmarks.csproj" -c Release -o /app/bench --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG TARGETARCH
ARG FFMPEG_VERSION=8.1
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl xz-utils \
    && rm -rf /var/lib/apt/lists/* \
    && case "${TARGETARCH:-$(dpkg --print-architecture)}" in \
         amd64) ffmpeg_arch=linux64 ;; \
         arm64) ffmpeg_arch=linuxarm64 ;; \
         *) echo "Unsupported Docker architecture: ${TARGETARCH:-unknown}" >&2; exit 1 ;; \
       esac \
    && archive="ffmpeg-n${FFMPEG_VERSION}-latest-${ffmpeg_arch}-gpl-shared-${FFMPEG_VERSION}.tar.xz" \
    && release_url="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest" \
    && curl --fail --location --retry 5 --retry-delay 2 \
         "${release_url}/${archive}" --output "/tmp/${archive}" \
    && curl --fail --location --retry 5 --retry-delay 2 \
         "${release_url}/checksums.sha256" --output /tmp/checksums.sha256 \
    && cd /tmp \
    && grep " ${archive}$" checksums.sha256 | sha256sum --check - \
    && mkdir -p /opt/ffmpeg \
    && tar --extract --xz --file="/tmp/${archive}" --directory=/opt/ffmpeg --strip-components=1 \
    && rm -f "/tmp/${archive}" /tmp/checksums.sha256
ENV PATH="/opt/ffmpeg/bin:${PATH}"
ENV LD_LIBRARY_PATH="/opt/ffmpeg/lib"
RUN ffmpeg -version \
    && ffprobe -version \
    && test -e /opt/ffmpeg/lib/libavcodec.so.62 \
    && test -e /opt/ffmpeg/lib/libavformat.so.62 \
    && test -e /opt/ffmpeg/lib/libavutil.so.60 \
    && test -e /opt/ffmpeg/lib/libswscale.so.9
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/bench ./bench
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "VDF.Web.dll"]
