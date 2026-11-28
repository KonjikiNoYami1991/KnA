# KnA dietro un reverse proxy

[English](REVERSE-PROXY.md)

Un reverse proxy (nginx, Caddy, Traefik, Nginx Proxy Manager…) mette il server web di KnA in HTTPS, su un nome di
dominio (`kna.example.com`) o sotto un percorso di un altro sito (`example.com/kna`). A KnA servono due impostazioni,
entrambe facoltative:

| Variabile | Significato |
|---|---|
| `KNA_TRUSTED_PROXIES` | Indirizzi o reti del proxy, separati da virgole: `172.18.0.2`, `172.18.0.0/16`, oppure `private` (tutte le reti private: 10/8, 172.16/12, 192.168/16, 100.64/10, fc00::/7). Questa macchina (127.0.0.1, ::1) è sempre fidata. |
| `KNA_BASE_PATH` | Il percorso sotto cui KnA è pubblicato, es. `/kna`. Non serve con un dominio tutto suo, né quando il proxy toglie il percorso e invia `X-Forwarded-Prefix` (lo fa lo StripPrefix di Traefik). |

Perché conta `KNA_TRUSTED_PROXIES`: KnA crede alle intestazioni `X-Forwarded-For` / `-Proto` / `-Host` / `-Prefix`
solo da questi indirizzi. Così il limite ai tentativi di accesso vede il client vero (non il proxy, che bloccherebbe
tutti dopo cinque password sbagliate di una sola persona), il cookie di sessione ha il flag `Secure` in HTTPS e il
percorso del cookie segue quello base. Le intestazioni di chiunque altro vengono ignorate, quindi un client non può
falsificarle. Quando le invia un proxy di cui KnA non si fida, il log di KnA lo dice con la riga da aggiungere:

```
X-Forwarded-* headers from 172.18.0.5 ignored: if it is your reverse proxy, set KNA_TRUSTED_PROXIES=172.18.0.5
```

**Docker**: quando il proxy è un altro container, KnA vede l'indirizzo della rete Docker, non 127.0.0.1: usa
l'indirizzo di quella riga del log, la rete (`docker network inspect …`) oppure `private`.

Cosa fa già KnA da solo:
- l'interfaccia web usa solo indirizzi relativi, quindi funziona sotto qualsiasi percorso; `/kna` viene reindirizzato
  a `/kna/`;
- l'avanzamento in tempo reale usa Server-Sent Events (non WebSocket) con `X-Accel-Buffering: no` e un segnale ogni
  15 s;
- i caricamenti partono a pezzi da 16 MB; quando il proxy li rifiuta (HTTP 413: nginx accetta 1 MB se non
  configurato), i pezzi si rimpiccioliscono da soli, fino a 256 KB. Alzare il limite del proxy resta più veloce.

Dove mettere le variabili: Docker → `environment:` in `docker-compose.yml`; servizio Linux → `/etc/kna/kna.env`
(poi `sudo systemctl restart kna`); servizio Windows → il valore `Environment` di
`HKLM\SYSTEM\CurrentControlSet\Services\KnA` nel registro (un `NOME=valore` per riga, poi riavvia il servizio).

## nginx

```nginx
# Sotto un percorso: https://example.com/kna/   (KNA_BASE_PATH=/kna)
location = /kna { return 301 /kna/; }
location /kna/ {
    proxy_pass http://127.0.0.1:8585;          # senza barra finale: il percorso resta
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_buffering off;                       # avanzamento e download man mano che arrivano
    proxy_request_buffering off;               # i caricamenti vanno dritti a KnA
    client_max_body_size 32m;                  # i pezzi di caricamento di KnA sono da 16 MB
    proxy_read_timeout 1h;
}
```

Con un dominio tutto suo usa `location / { … }` con le stesse righe e senza `KNA_BASE_PATH`. Con
`proxy_pass http://127.0.0.1:8585/;` (barra finale: percorso tolto) aggiungi
`proxy_set_header X-Forwarded-Prefix /kna;` invece di impostare `KNA_BASE_PATH`.

## Caddy

Caddy invia `X-Forwarded-For/-Proto/-Host` e trasmette i Server-Sent Events da solo; di base non ha limiti alla
dimensione delle richieste.

```caddy
kna.example.com {
    reverse_proxy 127.0.0.1:8585
}

# Sotto un percorso (KNA_BASE_PATH=/kna): "handle" mantiene il percorso, "handle_path" lo toglierebbe
example.com {
    redir /kna /kna/
    handle /kna/* {
        reverse_proxy 127.0.0.1:8585
    }
}
```

## Traefik (etichette Docker)

Traefik invia da solo le intestazioni `X-Forwarded-*`; il suo middleware StripPrefix aggiunge `X-Forwarded-Prefix`,
quindi `KNA_BASE_PATH` non serve.

```yaml
services:
  kna:
    # … il servizio del docker-compose.yml di KnA …
    environment:
      - KNA_TRUSTED_PROXIES=private          # oppure la rete di Traefik, es. 172.18.0.0/16
    labels:
      - traefik.enable=true
      - traefik.http.routers.kna.rule=Host(`kna.example.com`)
      - traefik.http.routers.kna.entrypoints=websecure
      - traefik.http.routers.kna.tls.certresolver=letsencrypt
      - traefik.http.services.kna.loadbalancer.server.port=8585
      # Sotto un percorso invece: rule=Host(`example.com`) && PathPrefix(`/kna`) e
      # - traefik.http.routers.kna.middlewares=kna-strip
      # - traefik.http.middlewares.kna-strip.stripprefix.prefixes=/kna
```

## Nginx Proxy Manager

1. **Hosts → Proxy Hosts → Add Proxy Host**: il dominio (es. `kna.example.com`), schema `http`, l'indirizzo di KnA e
   la porta `8585`. "Websockets Support" non serve (KnA usa Server-Sent Events), ma non dà fastidio.
2. **SSL**: richiedi un certificato, "Force SSL".
3. **Advanced** → Custom Nginx Configuration:
   ```nginx
   proxy_buffering off;
   proxy_request_buffering off;
   client_max_body_size 0;
   proxy_read_timeout 1h;
   ```
4. In KnA: `KNA_TRUSTED_PROXIES` = l'indirizzo da cui si collega NPM (te lo dice la riga del log qui sopra), oppure
   `private`.

Con NPM un dominio tutto suo è più semplice di un percorso; per un percorso usa una Custom Location `/kna` e
`KNA_BASE_PATH=/kna`.

## Single sign-on (Authelia, Authentik, oauth2-proxy…)

Quando il proxy fa già accedere le persone, KnA può prendere il nome utente che invia invece di chiedere una password:

| Variabile | Significato |
|---|---|
| `KNA_SSO` | `authelia` (intestazioni `Remote-User`, `Remote-Groups`), `authentik` (`X-authentik-username`, `X-authentik-groups`) oppure il nome dell'intestazione con il nome utente (es. `X-Auth-Request-User` per oauth2-proxy, `Cf-Access-Authenticated-User-Email` per Cloudflare Access) |
| `KNA_SSO_GROUPS_HEADER` | l'intestazione con i gruppi, quando non è quella del preset (separati da `,`, `\|` o `;`) |
| `KNA_SSO_ADMIN_GROUP` | i membri sono amministratori di KnA, gli altri utenti — allineato a ogni richiesta (l'ultimo amministratore resta sempre tale) |
| `KNA_SSO_USER_GROUP` | solo i membri di questo gruppo (o di quello degli amministratori) possono usare KnA |
| `KNA_SSO_CREATE` | `false` = solo chi ha già un account KnA; altrimenti l'account viene creato alla prima visita (come utente senza modalità avanzata; il primissimo account è l'amministratore) |
| `KNA_SSO_SECRET` | un valore che il proxy deve inviare anche in `X-KnA-Proxy-Secret` (altra intestazione: `KNA_SSO_SECRET_HEADER`) |
| `KNA_SSO_LOGOUT_URL` | dove porta "Esci" di KnA (la pagina di uscita del portale) |

L'intestazione conta **solo da `KNA_TRUSTED_PROXIES`**; senza di essa l'accesso di KnA (e HTTP Basic per gli script)
funziona come prima, ad esempio dalla rete locale direttamente sulla porta 8585. Due regole la rendono sicura:

- il proxy deve **sovrascrivere** l'intestazione (le integrazioni di Authelia/Authentik lo fanno: `authResponseHeaders`
  in Traefik, `auth_request_set` + `proxy_set_header` in nginx, `copy_headers` in Caddy), così un client non può
  inviarne una sua;
- se `KNA_TRUSTED_PROXIES` è un'intera rete (o `private`), chiunque al suo interno raggiunga direttamente la porta di
  KnA potrebbe inviare l'intestazione: fidati solo dell'indirizzo del proxy, tieni la porta chiusa agli altri, oppure
  imposta `KNA_SSO_SECRET` (il log di KnA lo segnala).

Gli account creati così sono segnati "SSO" in Impostazioni → Account; un amministratore può comunque dar loro una
password, le cartelle e la modalità avanzata. Chi accede tramite il portale non vede "Cambia password" (la password è
quella del portale).

**Authelia + Traefik**: il solito middleware `forwardAuth` con
`authResponseHeaders=Remote-User,Remote-Groups,Remote-Email,Remote-Name`, poi `KNA_SSO=authelia`,
`KNA_TRUSTED_PROXIES=<indirizzo di Traefik>`, eventualmente `KNA_SSO_ADMIN_GROUP=admins`.

**Authelia + nginx** (dentro la `location` di KnA, dopo lo snippet `auth_request` di Authelia):

```nginx
auth_request_set $user $upstream_http_remote_user;
auth_request_set $groups $upstream_http_remote_groups;
proxy_set_header Remote-User $user;
proxy_set_header Remote-Groups $groups;
proxy_set_header X-KnA-Proxy-Secret "un-valore-lungo-e-casuale";   # = KNA_SSO_SECRET
```

**Authentik** (provider proxy, outpost integrato o forward auth): `KNA_SSO=authentik`; i gruppi arrivano separati da
`|` in `X-authentik-groups`.

## Verifica

- Accedi attraverso il proxy e guarda il cookie di sessione nel browser: `Secure` in HTTPS, percorso `/` o quello
  base.
- Il log di KnA mostra l'indirizzo vero del client negli accessi falliti ("Failed sign-in for … from 203.0.113.7"),
  non quello del proxy.
- L'avanzamento di una codifica in corso scorre fluido (se avanza a scatti, il proxy accumula: `proxy_buffering off`).
