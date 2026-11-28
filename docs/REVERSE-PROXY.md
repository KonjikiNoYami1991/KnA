# KnA behind a reverse proxy

[Italiano](REVERSE-PROXY.it.md)

A reverse proxy (nginx, Caddy, Traefik, Nginx Proxy Manager…) puts KnA's web server on HTTPS, on a domain name
(`kna.example.com`) or under a path of another site (`example.com/kna`). KnA needs two settings for it, both optional:

| Variable | Meaning |
|---|---|
| `KNA_TRUSTED_PROXIES` | Addresses or networks of the proxy, comma separated: `172.18.0.2`, `172.18.0.0/16`, or `private` (every private network: 10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7). This machine (127.0.0.1, ::1) is always trusted. |
| `KNA_BASE_PATH` | The path KnA is published under, e.g. `/kna`. Not needed on a domain of its own, nor when the proxy strips the path and sends `X-Forwarded-Prefix` (Traefik's StripPrefix does). |

Why `KNA_TRUSTED_PROXIES` matters: KnA believes the `X-Forwarded-For` / `-Proto` / `-Host` / `-Prefix` headers only
from these addresses. Then the sign-in throttling sees the real client (not the proxy, which would lock everybody out
after five wrong passwords of one person), the session cookie gets the `Secure` flag on HTTPS, and the cookie path
follows the base path. Headers from anybody else are ignored, so a client can't fake them. When a proxy KnA does not
trust sends them, KnA's log says so with the line to add:

```
X-Forwarded-* headers from 172.18.0.5 ignored: if it is your reverse proxy, set KNA_TRUSTED_PROXIES=172.18.0.5
```

**Docker**: when the proxy is another container, KnA sees the address of the Docker network, not 127.0.0.1: use the
address from that log line, the network (`docker network inspect …`), or `private`.

What KnA already does by itself:
- the web UI uses relative addresses only, so it works under any path; `/kna` is redirected to `/kna/`;
- live progress is Server-Sent Events (not WebSockets) with `X-Accel-Buffering: no` and a heartbeat every 15 s;
- uploads are sent in 16 MB pieces; when the proxy refuses them (HTTP 413: nginx allows 1 MB unless configured), the
  pieces get smaller by themselves, down to 256 KB. Raising the proxy's limit is still faster.

Where to put the variables: Docker → `environment:` in `docker-compose.yml`; Linux service → `/etc/kna/kna.env`
(then `sudo systemctl restart kna`); Windows service → the `Environment` value of
`HKLM\SYSTEM\CurrentControlSet\Services\KnA` in the registry (one `NAME=value` per line, then restart the service).

## nginx

```nginx
# Under a path: https://example.com/kna/   (KNA_BASE_PATH=/kna)
location = /kna { return 301 /kna/; }
location /kna/ {
    proxy_pass http://127.0.0.1:8585;          # no trailing slash: the path is kept
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_buffering off;                       # live progress and downloads as they come
    proxy_request_buffering off;               # uploads go straight to KnA
    client_max_body_size 32m;                  # KnA's upload pieces are 16 MB
    proxy_read_timeout 1h;
}
```

On a domain of its own use `location / { … }` with the same lines and no `KNA_BASE_PATH`. With
`proxy_pass http://127.0.0.1:8585/;` (trailing slash: path stripped) add `proxy_set_header X-Forwarded-Prefix /kna;`
instead of setting `KNA_BASE_PATH`.

## Caddy

Caddy sends `X-Forwarded-For/-Proto/-Host` and streams Server-Sent Events by itself; it has no request size limit by
default.

```caddy
kna.example.com {
    reverse_proxy 127.0.0.1:8585
}

# Under a path (KNA_BASE_PATH=/kna): "handle" keeps the path, "handle_path" would strip it
example.com {
    redir /kna /kna/
    handle /kna/* {
        reverse_proxy 127.0.0.1:8585
    }
}
```

## Traefik (Docker labels)

Traefik sends the `X-Forwarded-*` headers by itself; its StripPrefix middleware adds `X-Forwarded-Prefix`, so
`KNA_BASE_PATH` is not needed.

```yaml
services:
  kna:
    # … the service of KnA's docker-compose.yml …
    environment:
      - KNA_TRUSTED_PROXIES=private          # or Traefik's network, e.g. 172.18.0.0/16
    labels:
      - traefik.enable=true
      - traefik.http.routers.kna.rule=Host(`kna.example.com`)
      - traefik.http.routers.kna.entrypoints=websecure
      - traefik.http.routers.kna.tls.certresolver=letsencrypt
      - traefik.http.services.kna.loadbalancer.server.port=8585
      # Under a path instead: rule=Host(`example.com`) && PathPrefix(`/kna`) and
      # - traefik.http.routers.kna.middlewares=kna-strip
      # - traefik.http.middlewares.kna-strip.stripprefix.prefixes=/kna
```

## Nginx Proxy Manager

1. **Hosts → Proxy Hosts → Add Proxy Host**: the domain (e.g. `kna.example.com`), scheme `http`, the address of KnA
   and port `8585`. "Websockets Support" is not needed (KnA uses Server-Sent Events), it does no harm.
2. **SSL**: request a certificate, "Force SSL".
3. **Advanced** → Custom Nginx Configuration:
   ```nginx
   proxy_buffering off;
   proxy_request_buffering off;
   client_max_body_size 0;
   proxy_read_timeout 1h;
   ```
4. In KnA: `KNA_TRUSTED_PROXIES` = the address NPM connects from (the log line above tells it), or `private`.

A domain of its own is simpler than a path with NPM; for a path use a Custom Location `/kna` and `KNA_BASE_PATH=/kna`.

## Checking

- Sign in through the proxy and look at the session cookie in the browser: `Secure` over HTTPS, path `/` or the base
  path.
- KnA's log shows the real client address on failed sign-ins ("Failed sign-in for … from 203.0.113.7"), not the
  proxy's.
- The progress of a running encode moves smoothly (if it jumps in bursts, the proxy buffers: `proxy_buffering off`).
