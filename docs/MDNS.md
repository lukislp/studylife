# Automatic discovery (mDNS / DNS-SD)

StudyLife can announce itself on the local network so that Home Assistant discovers it without
typing an address. The announcement is **opt-in** and **off by default**: with the default
configuration the server opens no multicast socket and sends nothing.

## What is announced

| | |
|---|---|
| Service type | `_studylife._tcp` (full name `_studylife._tcp.local.`) |
| Instance name | the configured `Name` (default `StudyLife`) |
| Port | the port of the advertised URL: 443 for `https` without an explicit port, 80 for `http` |
| TXT `version` | the server version |
| TXT `url` | the advertised base URL, scheme + host[:port], no trailing slash |
| TXT `https` | `true` or `false` |
| TXT `path` | `/` |

Nothing else goes on the wire: never an API key, token, user data or database detail. Anyone on the
same network segment can read the announcement - it contains only what the TXT table above lists.

The advertised `url` is **configuration, not derived from the host**. A StudyLife server usually
runs behind a reverse proxy, ingress or gateway under a name like `https://studylife.example.org`,
and that is the address Home Assistant has to connect to - not the address of the container or pod.

## Configuration

| Key | Environment variable | Default | Meaning |
|---|---|---|---|
| `Discovery:Mdns:Enabled` | `Discovery__Mdns__Enabled` | `false` | Announce while serving normally |
| `Discovery:Mdns:Url` | `Discovery__Mdns__Url` | none | Advertised base URL. Required when `Enabled` or `Only`; must be an absolute `http`/`https` URL, otherwise the server refuses to start with a clear message |
| `Discovery:Mdns:Name` | `Discovery__Mdns__Name` | `StudyLife` | Instance name (max. 63 bytes) |
| `Discovery:Mdns:Only` | `Discovery__Mdns__Only` | `false` | Run **only** the announcer: no database, no migrations, no Redis, no web endpoints |

Binding is best effort. Without multicast (for example a container on a bridge network) the server
logs a warning, keeps running and simply is not discoverable - discovery is a nicety, never a reason
to fail. Only the instance name is operator-chosen; there is no conflict probing, so do not announce
two servers under the same name.

mDNS needs the **host's** network interfaces. A container on Docker's default bridge network or a
Kubernetes pod on the pod network cannot reach the LAN with multicast. Pick one of the setups below.

## Setup 1: plain Docker with host networking

Single container, announcing itself:

```bash
docker run -d --name studylife --network host \
  -e Discovery__Mdns__Enabled=true \
  -e Discovery__Mdns__Url=https://studylife.example.org \
  <your usual options and image>
```

With `--network host` the server listens on the host's port 8080 directly (no `-p` mapping).

## Setup 2: docker-compose

```yaml
services:
  studylife:
    image: ghcr.io/lukislp/studylife-server:latest
    network_mode: host
    environment:
      Discovery__Mdns__Enabled: "true"
      Discovery__Mdns__Url: "https://studylife.example.org"
      # Discovery__Mdns__Name: "StudyLife"
```

If the server itself must stay on a bridge network (for example behind the compose load balancer
of `docker-compose.scale.yml`), run a second, announcer-only container with host networking next to it:

```yaml
  studylife-mdns:
    image: ghcr.io/lukislp/studylife-server:latest
    network_mode: host
    environment:
      Discovery__Mdns__Only: "true"
      Discovery__Mdns__Url: "https://studylife.example.org"
    healthcheck:
      disable: true   # announcer-only mode serves no HTTP, the image's HEALTHCHECK would fail
    restart: unless-stopped
```

## Setup 3: Kubernetes

The web pods run N replicas on the pod network, where multicast cannot reach the LAN. One dedicated
pod with `hostNetwork` announces on behalf of the ingress URL: `k8s/optional/studylife-mdns.yaml`.

It is deliberately **not** applied by `bootstrap-cluster.ps1` and not part of the Flux
Kustomization. Apply it by hand when you want it:

```bash
# edit Discovery__Mdns__Url (and optionally the nodeSelector) first
kubectl apply -f k8s/optional/studylife-mdns.yaml
```

What it needs from you:

1. `Discovery__Mdns__Url` - the ingress/gateway URL Home Assistant connects to (the file ships with the placeholder `https://studylife.example.org`).
2. Optionally a `nodeSelector`, so the pod lands on a node in the same LAN/VLAN as Home Assistant.

The manifest creates its own namespace `studylife-mdns` because the Pod Security level `baseline`
of `studylife-scale` forbids `hostNetwork`. The pod runs the same image as the web pods in
`Only` mode, as a non-root user, with a read-only root filesystem, no capabilities, no service
account token, no Secret and no ConfigMap. It has no probes: the image's Docker `HEALTHCHECK` is
ignored by Kubernetes, and a hostNetwork pod must not claim a fixed HTTP port on the node.

## VLANs and routers

mDNS is link-local multicast: it does not cross VLANs or subnets on its own.

- Keep the announcing host and Home Assistant in the same VLAN, or
- let the router reflect mDNS between them. On **UniFi** set the mDNS option to **Auto**, or add `_studylife._tcp` as a custom service in the mDNS settings. Other routers call this an mDNS reflector, repeater or Avahi reflector.

## What Home Assistant offers

With the matching Home Assistant integration installed ([studylife-hacs](https://github.com/lukislp/studylife-hacs)),
the server shows up under *Settings, Devices & services* as a discovered StudyLife server. Home
Assistant reads the `url` from the announcement, so you only confirm it and enter the per-user API
key from the Setup page (see the README's Security section) - the key is never part of the
announcement. Without discovery nothing changes: the integration can still be set up by entering the
URL manually.

## Troubleshooting

- Look for `mDNS-Ankündigung aktiv` in the server log. A `Der Server läuft ohne Discovery weiter` warning means nothing could be bound.
- From a Linux machine in the same VLAN: `avahi-browse -rt _studylife._tcp` lists the instance and its TXT records.
- Nothing discovered although the log says active: check the VLAN/reflector note above and that UDP 5353 multicast (224.0.0.251, ff02::fb) is not filtered by a host firewall.
