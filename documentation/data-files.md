# Upload de fichiers temporaires via `/data/files`

Cette page explique comment utiliser la fonctionnalité **d’upload de fichiers temporaires** de SlimFaas via les routes **`/data/files`**, et comment cela fonctionne “derrière” (réplication, stockage, limites mémoire), **sans dépendre d’un langage d’implémentation**.

---

## À quoi sert `/data/files` ?

`/data/files` est un petit service de **stockage temporaire** (binaire) pensé pour :

- déposer des artefacts (zip, pdf, audio, pptx, etc.) utilisés par des fonctions / jobs / agents,
- partager un fichier entre composants d’un même environnement,
- **distribuer** un fichier dans un cluster SlimFaas sans faire transiter le binaire dans le moteur de consensus.

**Principe important :**
- le **contenu** du fichier est stocké **sur disque local** (sur les nœuds),
- les **métadonnées** (type MIME, taille, empreinte SHA256, nom de fichier, expiration) sont stockées dans **SlimData (consensus/RAFT)** pour garantir une vue cohérente dans le cluster.

---

## Résumé des routes

Base : `/data/files`

| Méthode | Route | Description |
|---|---|---|
| `POST` | `/data/files?id={id?}&ttl={ms?}` | Upload d’un fichier (corps HTTP = octets bruts) |
| `GET` | `/data/files/{id}` | Téléchargement (et récupération automatique depuis un autre nœud si absent localement) |
| `DELETE` | `/data/files/{id}` | Suppression des métadonnées (le fichier devient inaccessible via l’API) |
| `GET` | `/data/files` | Liste des fichiers connus (via leurs métadonnées) |

> ⚠️ Visibilité / sécurité
> Ces routes sont soumises au même mécanisme de visibilité que le reste de `/data/*` (voir section **Sécurité & visibilité**).

---

## 1) Upload : `POST /data/files`

### Contrat d’appel

- Corps HTTP : **octets bruts** (ce n’est pas un upload multipart/form-data)
- Header **obligatoire** : `Content-Length`
- Headers recommandés :
    - `Content-Type` : type MIME (sinon `application/octet-stream`)
    - `Content-Disposition` : pour fournir un nom de fichier de téléchargement

Query string :

- `id` (optionnel) : identifiant du fichier
    - si absent → SlimFaas génère un id
    - format autorisé : lettres/chiffres + `._-` (longueur limitée)
- `ttl` (optionnel) : durée de vie en **millisecondes**
    - ex : `ttl=600000` (10 minutes)

### Pourquoi `Content-Length` est obligatoire ?

SlimFaas ne “bufferise” pas le fichier en RAM applicative : il **stream** le flux vers le disque.
Pour appliquer ses garde-fous (notamment la limite de transferts parallèles), SlimFaas a besoin de connaître la **taille** à l’avance. Si l’appel est en “chunked” (taille inconnue), l’upload est refusé.

### Réponses

- `200 OK` : corps = l’`id` du fichier (texte)
- `400 Bad Request` : `id` invalide
- `411 Length Required` : `Content-Length` manquant/inconnu

### Exemples (curl)

Upload d’un fichier en conservant un nom de téléchargement :

```bash
curl -X POST "http://<slimfaas>/data/files?ttl=600000"   -H "Content-Type: application/pdf"   -H "Content-Disposition: attachment; filename="report.pdf""   --data-binary @./report.pdf
```

Upload avec un id imposé :

```bash
curl -X POST "http://<slimfaas>/data/files?id=my-file-001&ttl=300000"   -H "Content-Type: application/octet-stream"   --data-binary @./payload.bin
```

---

## 2) Download : `GET /data/files/{id}`

### Ce que fait SlimFaas

Quand vous téléchargez un fichier :

1. SlimFaas lit ses **métadonnées** (cohérentes cluster) associées à l’`id`
2. Il vérifie si le fichier est déjà présent **localement** et conforme (empreinte)
3. S’il est absent, SlimFaas tente de le **récupérer depuis un autre nœud** du cluster
4. Il **stream** le fichier en réponse HTTP

### Réponses

- `200 OK` : contenu du fichier en streaming
    - `Content-Type` : depuis les métadonnées (sinon `application/octet-stream`)
    - `Content-Disposition` : nom de fichier (si disponible)
- `400 Bad Request` : `id` invalide
- `404 Not Found` : métadonnées absentes/expirées, ou aucun nœud n’a le binaire

### Exemple

```bash
curl -L "http://<slimfaas>/data/files/my-file-001" -o my-file-001.bin
```

---

## 3) Suppression : `DELETE /data/files/{id}`

Supprime **les métadonnées** du fichier.

- `204 No Content` : succès
- `400 Bad Request` : `id` invalide

> Important
> Supprimer les métadonnées rend le fichier **inaccessible via l’API** (un `GET` retournera 404), même si le binaire existe encore sur disque. Le nettoyage disque dépend des mécanismes de **cleanup/expiration** (voir plus bas).

---

## 4) Liste : `GET /data/files`

Retourne la liste des fichiers connus **par leurs métadonnées** (donc cohérent cluster) et leur éventuelle date d’expiration.

---

## Sécurité & visibilité

Les routes `/data/*` (dont `/data/files`) peuvent être configurées en :

- **Public** : accessible à tous
- **Private** (souvent le défaut) : accessible uniquement aux appels considérés “internes” (réseau interne, gateway interne, etc.)

Dans un contexte “Private”, les accès externes reçoivent typiquement **404 Not Found** (volontairement discret).

Conséquence : `/data/files` est plutôt conçu pour des usages internes (workflows, agents, fonctions), pas comme un service public de partage de fichiers.

---

## Comment ça marche derrière (sans détails d’implémentation)

### A) Métadonnées (cohérentes cluster) vs contenu (sur disque)

- Les **métadonnées** (type, taille, empreinte, nom, TTL) sont enregistrées dans un stockage “cluster” (consensus/RAFT).
- Le **contenu** du fichier est écrit sur disque sur le nœud qui reçoit l’upload.
- Les autres nœuds récupèrent le contenu au besoin.

Avantage : le consensus reste léger (pas de gros transferts binaires), tout en gardant une vue cohérente des fichiers existants.

### B) Réplication : “announce” puis “pull”

Au lieu d’envoyer directement le fichier à tous les nœuds pendant l’upload :

1. Le nœud qui reçoit l’upload annonce “j’ai le fichier {id} avec l’empreinte {sha}”
2. Les autres nœuds mettent cette annonce en file d’attente
3. Un worker en arrière-plan **télécharge** le fichier depuis un nœud qui le possède (pull)
4. En plus, si un client demande un fichier sur un nœud qui ne l’a pas, ce nœud peut aussi le **pull** à la demande

C’est un modèle **best effort** et **éventuellement cohérent** : la diffusion n’est pas forcément instantanée, mais elle converge si les nœuds restent disponibles.

---

## Limite de sécurité : 256 Mo en parallèle (très important)

SlimFaas applique une protection : **au maximum ~256 Mo de transferts en parallèle** (tous fichiers confondus).

- Chaque upload/téléchargement interne “réserve” un budget correspondant à sa taille.
- Si démarrer un nouveau transfert dépasserait le budget, il attend.
- Cas particulier : un fichier plus gros que 256 Mo peut passer **seulement s’il est seul** (pas d’autres transferts en cours).

Ce garde-fou réduit le risque d’emballement (beaucoup de gros transferts simultanés) mais **ne garantit pas** que la mémoire du pod reste sous 256 Mo (voir section suivante).

---

## ⚠️ Mémoire : le noyau Linux peut monter en RAM l’équivalent des fichiers

Même si SlimFaas écrit et lit les fichiers en streaming (petits buffers), **Linux utilise le “page cache”** : le noyau garde en mémoire une partie des données lues/écrites sur disque.

Dans Kubernetes/containers, cela se manifeste souvent comme :

- la mémoire applicative (managed) reste raisonnable,
- mais la mémoire du conteneur (cgroup/RSS) augmente fortement,
- et peut ne pas redescendre immédiatement.

### Conséquences pratiques

Sur des fichiers volumineux (ex. 200+ Mo), vous pouvez observer :

- un pic mémoire pendant l’upload (écriture → cache),
- un autre pic quand d’autres nœuds pullent le fichier (lecture → cache),
- une mémoire qui met du temps à revenir à la normale → risque d’**OOMKill** si la limite mémoire du pod est trop serrée.

### Recommandation de sizing (règle simple)

Prévoyez assez de mémoire pour :

- votre baseline applicative,
- + les transferts parallèles (limités à 256 Mo),
- + le page cache Linux (souvent proche de la taille des fichiers manipulés).

Un point de départ raisonnable :

- `memory limit >= baseline + (2 × taille max des fichiers manipulés en même temps)`

Puis ajustez selon vos métriques et vos workloads.

---

## Stockage & persistance

Les fichiers sont stockés sur le disque local de chaque nœud, dans un répertoire configuré côté SlimFaas.

- Si ce stockage est **éphémère**, un redémarrage de pod peut faire perdre les binaires.
- Pour une meilleure durabilité, utilisez un **volume persistant**.
- Le système peut re-télécharger un fichier depuis un autre nœud **tant qu’au moins un nœud l’a encore** et que les métadonnées existent.

---

## Expiration & nettoyage

Si vous utilisez `ttl` :

- les métadonnées expirent automatiquement,
- le fichier devient invisible/non téléchargeable via l’API.

Le nettoyage du disque (suppression des binaires expirés) dépend de votre configuration de “cleanup” côté SlimFaas.

---

## Dépannage

### `411 Length Required`

Votre client/proxy envoie l’upload sans `Content-Length` (souvent à cause du mode chunked).
Solution : envoyer un fichier avec taille connue (par exemple `curl --data-binary @file`) et vérifier la config proxy.

### `404 Not Found` sur `GET`

- métadonnées supprimées/expirées,
- fichier non disponible sur les nœuds (nettoyé, perdu au redémarrage, stockage éphémère),
- incohérence temporaire (le pull n’a pas encore eu lieu).

### “La mémoire monte et ne redescend pas”

Souvent dû au page cache Linux.
Augmentez la limite mémoire, réduisez la concurrence, ou adaptez la stratégie de stockage. Surveillez la mémoire cgroup/RSS et l’activité disque.

---

## Checklist “production”

- [ ] Proxy compatible upload binaire **avec Content-Length**
- [ ] `MaxRequestBodySize` (Kestrel / ingress) compatible avec vos tailles de fichiers
- [ ] Stockage rapide et dimensionné (quota + IOPS)
- [ ] Limite mémoire pod adaptée (page cache + transferts)
- [ ] Stratégie de nettoyage disque (TTL + cleanup)
- [ ] Monitoring : mémoire cgroup, disque, taux/volume de transferts
