# ADR-0013: Co-located containers and shared public ingress

- Status: Proposed for the VPS deployment
- Date: 2026-11-28

## Context

PersonalFlow will share a Linux VPS with unrelated Docker services, some of which may also be public and may run their own PostgreSQL containers. Compose service names, bridge subnets, named volumes, memory/CPU use, and host-published ports are all potential collision points. An additional PostgreSQL server must never be mistaken for or able to access PersonalFlow's database.

## Decision

- Keep the PersonalFlow Compose project explicitly named and scope its stateful volume names to PersonalFlow.
- Separate containers by network role: APIs and Caddy share the private proxy network; PostgreSQL, API database clients, and one-shot migration jobs use an internal data network; Caddy and other public HTTP frontends use a shared `personalflow_edge` network. Caddy does not join the database network. PostgreSQL does not publish a host port.
- Treat PersonalFlow Caddy as the one host-level HTTP/HTTPS entrypoint. Other public HTTP services attach only their web frontend to `personalflow_edge` and add a unique host route in the ignored `local-private/caddy-sites/` directory. Their databases stay on their own internal networks. Do not publish another container on host ports 80/443.
- Preflight checks the fixed private proxy subnet against Docker networks, host listeners, and port mappings on all containers, including stopped ones. It fails before deployment if an overlap exists. Caddy site snippets are validated before the release starts.
- Keep per-container CPU and memory caps configurable. These cap PersonalFlow's maximum usage but do not reserve host capacity; choose a VPS with headroom for the other stacks and monitor it.

## Consequences

- An unrelated stack can use a `postgres` Compose service and its own named volume without sharing PersonalFlow's network or data volume.
- A second reverse proxy cannot bind 80/443 alongside PersonalFlow Caddy. If another proxy already owns those ports, consolidate routes behind one designated edge proxy before deploying; the current Compose setup deliberately fails preflight instead of competing.
- Additional public services can be routed through PersonalFlow Caddy without joining its private API/database networks. The shared edge network is a trust boundary for those web containers; their databases must not be attached to it.
- The private proxy subnet may need to be changed if an existing Docker network overlaps it. Once deployed, keep its network name and subnet stable.
- The VPS configuration is prepared but has not yet been exercised with a second application stack or on the real server.

## Revisit when

- The VPS and its existing public reverse proxy are selected.
- Another public service is actually added.
- A separate host-level edge proxy is required instead of PersonalFlow Caddy.
