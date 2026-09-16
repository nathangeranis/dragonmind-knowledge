FROM apache/age:release_PG16_1.6.0

USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends postgresql-16-pgvector \
    && rm -rf /var/lib/apt/lists/*
USER postgres
