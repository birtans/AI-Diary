# Shared identity server image — Keycloak with realms and themes baked in.
# Not AI Diary–specific: every project's realm goes into data/import and its
# theme into themes/, and --import-realm picks up every JSON file in import/.
#
# Build context is this folder (keycloak/).

FROM quay.io/keycloak/keycloak:26.1 AS builder

# Build-time options — baked into the optimized server, not passed at start.
ENV KC_DB=postgres
ENV KC_HEALTH_ENABLED=true

RUN /opt/keycloak/bin/kc.sh build

FROM quay.io/keycloak/keycloak:26.1

COPY --from=builder /opt/keycloak/ /opt/keycloak/

# One file per realm. --import-realm only seeds a realm that doesn't exist
# yet, so realm changes after first boot must be applied through the admin
# console or API.
COPY realm-export.json /opt/keycloak/data/import/diary-realm.json
COPY themes/ /opt/keycloak/themes/

ENTRYPOINT ["/opt/keycloak/bin/kc.sh"]
CMD ["start", "--optimized", "--import-realm"]
