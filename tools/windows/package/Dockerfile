# The caller verifies the local base image's content ID before building.
FROM cod2-build-win
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      openjdk-17-jre-headless osslsigncode xauth xvfb \
 && rm -r /var/lib/apt/lists/*
