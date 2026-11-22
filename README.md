# JellyUSB : plugin Jellyfin pour une médiathèque sur disques USB

Plugin pour **Jellyfin 12.4** (.NET 10). Les films sont rangés sur des disques USB montés sous
`/mnt/usb/<MediaID>/`. Le **MediaID** est le nom du dossier qui suit `/mnt/usb/`, c'est-à-dire le nom
du support qui a servi à l'import (`MOVIES00`, `MOVIES01`… `MOVIES99`).

## Ce que fait le plugin

1. **La navigation ne change pas.** Bibliothèque et collections restent consultables même quand les
   disques sont débranchés.
   **Un disque absent ne fait jamais retirer ses films**, même débranché pendant un scan (voir
   « Protection de la bibliothèque »).
2. **Vérification avant lecture.** Au moment de lancer un film situé sous `/mnt/usb/`, le serveur
   vérifie que le support est branché et que le fichier existe. Sinon la lecture est bloquée et le
   client affiche :
   - **Support absent** : « *Titre* » n'est pas présent sur le serveur. Insérez le support
     **MOVIES03** puis relancez la lecture.
   - **Fichier introuvable** : « *Titre* » est introuvable sur le support **MOVIES03** (le fichier a
     peut-être été déplacé ou renommé).
3. **Accents réparés.**
   - **À l'import**, les NFO écrits en Windows‑1252/Latin‑1 sont lus correctement.
   - **À chaque ajout ou actualisation**, le double encodage (« Ã© ») et les « � » sont réparés quand
     c'est possible.
   - **Pour l'existant**, une tâche planifiée répare ce qui est déjà en base.
4. **Disque visible sur la fiche Android TV.** Dans l'application Android TV, le résumé d'un film
   commence par son disque et l'état de celui-ci : « 🟢 Disque **MOVIES03** branché » ou « 🔴 Disque
   **MOVIES03** absent : branchez-le pour lire ce film ». C'est ajouté seulement dans ce que reçoit la
   TV : la base et les NFO ne changent pas.
5. **Vue par MediaID.** Dans une bibliothèque de films, le menu des vues (en haut à gauche, à côté de
   « Films », « Suggestions »…) propose « MediaID » : une ligne par support USB avec son image de fond,
   puis la page de chaque support avec tous ses films.

Le plugin ne modifie **jamais** les fichiers des disques, ne surveille pas les branchements et ne
lance aucun scan de lui-même.

## Protection de la bibliothèque

Pendant un scan, Jellyfin retire les films d'un dossier qui revient sans eux. Or il lit un dossier
introuvable comme un dossier vide. Un disque débranché **pendant** un scan, après la lecture de la
racine de son support, faisait donc retirer ses films. Ils revenaient au rebranchement, mais
ré-importés : nouvelle date d'ajout et nouvelle analyse. Ce cas a été reproduit avec la 1.4.2.

Depuis la 1.4.3, sous le chemin de base :

| Cas | Ce que le plugin répond à Jellyfin | Résultat |
|---|---|---|
| Racine d'un support absent depuis un moment (`/mnt/usb/MOVIES03` vide) | un dossier vide | Jellyfin saute ce dossier de bibliothèque, sans erreur dans le journal |
| Racine d'un support qui vient de disparaître (moins de 15 min) | une erreur d'entrée-sortie | Jellyfin n'y touche pas pendant ce scan |
| Tout dossier à l'intérieur d'un support absent, qui disparaît ou qui renvoie « No such device » | une erreur d'entrée-sortie, jamais une liste vide ou tronquée | Jellyfin garde les films |
| Bibliothèque montée sur `/mnt/usb` lui-même, support absent | une erreur d'entrée-sortie | les films du support sont gardés |
| Dossier vraiment supprimé d'un disque branché | « dossier introuvable », comme Jellyfin | le film est retiré, normalement |

Les chemins hors du chemin de base ne sont pas concernés. Cette protection prolonge une classe
interne de Jellyfin. Sur une autre version que la 12.4, ou si cette classe a changé, elle ne se met
pas en place, et le journal le dit au démarrage (« protection des supports absents désactivée ») :
Jellyfin démarre normalement, sans la protection. Le script
`tests/integration/unplug-checks.py` rejoue le scénario : il débranche un support, fait relire son
dossier, lance un scan complet, puis rebranche le support.

## Comportement selon le client (testé)

Tests du 21/11/2026 : instance Jellyfin 12.4 jetable, émulateurs Android, Kodi sur écran virtuel.

| Client | Ce que voit l'utilisateur quand le support est absent |
|---|---|
| **Interface web** (navigateur), Jellyfin Media Player | Une seule fenêtre, **« Support absent »**, au design de Jellyfin. Depuis la 1.5.3, le script JellyUSB lit le message dans la réponse du serveur et l'écrit dans la fenêtre « Erreur de lecture » de jellyfin-web avant son affichage : la fenêtre générique n'apparaît plus du tout, même sans connexion temps réel. |
| **App Android 2.7.3**, lecteur « Web » | Comme l'interface web |
| **App Android 2.7.3**, lecteur intégré (par défaut) | Le lecteur reste noir, sans texte (l'app n'affiche pas d'erreur). En revenant en arrière, la fenêtre **« Support absent »** attend dans l'app. Un seul « Compris ». |
| **Android TV 0.20.0** | Avant de lancer : la fiche du film indique le disque et s'il est branché (depuis la 1.5). En lançant : toast d'environ 3 s, « Insérez le support MOVIES03 : « *Titre* » est absent du serveur. » (texte court, modifiable). Depuis la 1.5.1, le serveur referme ensuite le lecteur, qui restait sinon sur un écran noir : la fiche revient d'elle-même. |
| **Findroid 1.2.0** | Toast technique « Index 0 out of bounds… » : Findroid n'affiche pas les messages du serveur |
| **Kodi 21.3 + jellyfin-kodi 2.2.0** (mode « add-on ») | Notification **« Support absent »** : « Insérez le support MOVIES03 : « *Titre* » est absent du serveur. » (environ 9 s, texte défilant). Pas d'autre erreur à l'écran. |
| **Kodi 21.3 + JellyCon 1.0.2** | Notification « Connection Error / NotAllowed » (JellyCon), puis notre texte court dans une notification titrée « JellyCon » (JellyCon remplace notre titre) |
| **Swiftfin** (iOS) | Erreur générique (Swiftfin n'affiche pas les messages du serveur). Non testable ici. |
| Tous | Les requêtes de flux, HLS et téléchargement sont refusées immédiatement (pas d'attente de 4 s sur un disque absent) |

Les films présents se lisent normalement dans tous les clients testés.

Limites :
- Kodi en mode « chemins natifs » lit les fichiers sans passer par le serveur, donc le plugin ne peut
  pas intervenir.
- Si l'app Android a perdu sa connexion temps réel avec le serveur, le message ne peut pas lui être
  envoyé : seul le refus de lecture reste.

## Installation

### Depuis le dépôt de plugins (recommandé)

1. *Tableau de bord > Extensions > Dépôts* (« Gérer les dépôts ») > **+**
   - Nom : `JellyUSB`
   - URL : `https://raw.githubusercontent.com/webbackuper/jellyusb/main/manifest.json`
2. *Extensions > Catalogue* : installer **JellyUSB**, puis redémarrer Jellyfin.

Les nouvelles versions apparaissent ensuite dans le tableau de bord.

### Depuis les sources

Prérequis : SDK .NET 10 (`sudo apt install dotnet-sdk-10.0`) et Jellyfin installé par les paquets
Debian/Ubuntu (le projet référence `/usr/lib/jellyfin/bin/MediaBrowser.XbmcMetadata.dll`).

```bash
dotnet build -c Release
```
```bash
dotnet test
```
```bash
sudo ./install.sh
```

Ce que fait `install.sh` :
1. arrête Jellyfin ;
2. **sauvegarde la base** (`/var/lib/jellyfin/data/jellyusb-backup-<date>/`) ;
3. copie le plugin dans `/var/lib/jellyfin/plugins/JellyUSB_<version>/` ;
4. déclare le dépôt de plugins JellyUSB (fiche du plugin et mises à jour dans le tableau de bord) ;
5. redémarre Jellyfin et vérifie que le plugin est chargé.

### Désinstallation

Le bouton **Désinstaller** (page JellyUSB ou *Extensions*) ou `sudo ./install.sh --uninstall` remet
Jellyfin dans l'état d'avant JellyUSB :

| Modification | Retirée à la désinstallation |
|---|---|
| Dossier du plugin (dll, image) | oui (par Jellyfin) |
| Script de l'interface web, blocage de la lecture, ligne sur la fiche TV, tuiles MediaID (web et TV), protection des supports | oui, au redémarrage que Jellyfin demande (rien n'est écrit dans les fichiers de Jellyfin) |
| Lecteur « JellyUSB NFO » dans l'ordre des lecteurs des bibliothèques | oui |
| Dépôt de plugins JellyUSB dans les réglages du serveur | oui |
| Réglages du plugin, dossier cache, fichiers de la tâche planifiée | oui |
| Titres réparés (accents) dans la base | **non**, ce sont des corrections. La base d'avant l'installation peut être restaurée depuis `data/jellyusb-backup-*`, mais les changements faits depuis seront perdus |
| Sauvegardes `data/jellyusb-backup-*` (créées par `install.sh`) | avec `sudo ./install.sh --uninstall --purge` |
| Montage des disques (`os/`) | à part : `sudo ./os/install-os.sh --uninstall` |
| Fichiers des disques USB | rien à retirer : le plugin n'y écrit jamais |

Après l'installation :
1. Ouvrir *Tableau de bord > Extensions > JellyUSB* et vérifier les réglages.
2. Brancher les supports dont les titres sont abîmés, puis lancer *Tableau de bord > Tâches
   planifiées > JellyUSB > Réparer les titres mal encodés*. Ensuite, la tâche se relancera après chaque scan (option
   désactivable).

### Montage des disques (côté système)

Voir [os/README.md](os/README.md) : une règle udev monte tout disque nommé `MOVIES00` à `MOVIES99`
(NTFS, exFAT, FAT, ext4, xfs, btrfs) sur `/mnt/usb/<label>`, et supprime le dossier au
débranchement. C'est recommandé : avec un automontage (`x-systemd.automount` dans `/etc/fstab`), un
disque absent répond « No such device » après plusieurs secondes d'attente. Jellyfin 12.4 laisse
alors **tout** le scan de bibliothèque échouer. Depuis la version 1.2, JellyUSB traite ce dossier
comme vide : le scan continue et les films du disque absent sont conservés. Les scans restent
toutefois ralentis par l'attente de l'automontage.

## Tâches planifiées : « En cours d'exécution » et supports USB

Depuis la 1.3, la page *Tableau de bord > Tâches planifiées* commence par un panneau JellyUSB mis à
jour toutes les 2 s :
- les tâches en cours d'exécution, avec leur progression ;
- pour chaque support : **absent**, **libre** (peut être démonté) ou **occupé**, avec ce qui l'occupe :
  processus ffmpeg et sa fonction probable (recherche des génériques par Intro Skipper, images,
  normalisation audio, transcodage…), fichier ouvert, durée, lectures en cours.

Seuls les processus de Jellyfin sont visibles. Pour un autre programme : `sudo fuser -vm /mnt/usb/MOVIES02`.
Les données viennent de `GET /JellyUsb/Activity`, réservé aux administrateurs.

Le panneau et la fermeture automatique de la fenêtre générique viennent d'un petit script que le
plugin ajoute à la volée à `index.html` de jellyfin-web. Aucun fichier de Jellyfin n'est modifié, et
le script se désactive dans la page de configuration.

## Vue « MediaID » d'une bibliothèque de films

**Sur la TV (application Android TV)**, qui n'a ni le bandeau ni le menu des vues : une tuile
**MediaID** (image du plugin) apparaît dans « Mes médias », après les bibliothèques. Elle ouvre la liste
des disques, chacun avec son image de fond. Un disque s'ouvre sur ses films. Pour l'application, c'est
une bibliothèque de collections (un disque = une collection), mais elle n'existe que dans les réponses
du serveur à la TV : rien n'est créé en base, et les films sont les vrais. Le texte d'un disque dit
s'il est branché et combien il a de films.

Dans l'interface web, trois accès :
- la tuile **MediaID** dans « Mes médias » de la page d'accueil, après les bibliothèques ;
- le bouton **Media** du bandeau du haut, après les bibliothèques (Favoris, Films, Collections…), avec
  une icône de disque dur. Il ouvre la vue de la première bibliothèque de films qui a des supports USB,
  et s'affiche en bleu, comme le bouton d'une bibliothèque ouverte, tant qu'on est dans la vue. Il n'y
  a pas de bandeau sur téléphone ni sur tablette : utilisez alors le second accès ;
- dans une bibliothèque de films, l'entrée **MediaID** du menu en haut à gauche.
- **Une ligne par support** (MOVIES00, MOVIES01…), comme les lignes des Suggestions. Chaque ligne
  donne le nombre de films et l'état du support (**branché** ou **absent**), puis montre l'image de fond du
  support et ses derniers films ajoutés. Les affiches d'un support absent sont grisées.
- **Un clic sur le nom ou l'image d'un support** ouvre la page de ce support : tous ses films, par
  ordre alphabétique, chargés au fil du défilement.
- **Retour** : le bouton « ‹ MediaID » en haut de la page d'un support ramène à la liste des supports.
  Le bouton Précédent du navigateur marche aussi, y compris depuis la fiche d'un film. Choisir une
  autre vue dans le menu (Films, Genres…) quitte la vue MediaID.

L'image de fond est générée par le plugin : un boîtier de disque 2,5" USB étiqueté avec le MediaID, sur un dégradé dont
la couleur dépend du numéro du support (deux supports voisins ont des couleurs bien différentes). Elle
est servie par `GET /JellyUsb/Media/{MediaID}/Background` (SVG).

Tout vient de la base de Jellyfin : la vue s'affiche aussi quand les disques sont débranchés, et elle
ne touche jamais les disques. Un support est une racine de la bibliothèque située sous le chemin de
base (`/mnt/usb/MOVIES02`) ; si la racine est le chemin de base lui-même (`/mnt/usb`), ses sous-dossiers
sont les supports. La vue n'apparaît que dans les bibliothèques qui ont au moins un support.

La vue n'existe que dans l'interface web, et dans les applications qui l'affichent (Jellyfin Media
Player, jellyfin-android). Les applications natives (Android TV, Kodi, Swiftfin…) ne la proposent pas.

## Réglages (page de configuration)

Les réglages essentiels sont en haut de la page : vérification avant lecture, chemin de base, disque
sur la fiche Android TV, texte du message TV, fermeture du lecteur TV. Tout le reste est dans
**Réglages avancés**, replié par défaut.

La page s'ouvre directement depuis le menu de gauche du tableau de bord, section **Extensions >
JellyUSB**. Elle reprend la fiche de l'extension :
- en haut, le titre, la description et l'image ;
- à droite, l'activation, la désinstallation, le statut, la version, le développeur et le dépôt ;
- au centre, deux onglets : **Paramètres** et **Historique des versions**. L'historique liste chaque
  version avec sa date et ses nouveautés, et permet d'en installer une autre.

| Réglage | Défaut | Rôle |
|---|---|---|
| Vérifier la présence du support | oui | Active le blocage avant lecture |
| Chemin de base | `/mnt/usb/` | Le MediaID est le premier dossier sous ce chemin |
| Titres et messages | voir plus haut | Variables `{title}`, `{mediaId}`, `{path}` |
| Affichage web | message au premier plan | Autre choix : notre texte dans la fenêtre « Erreur de lecture » de jellyfin-web, qui reste alors recouverte par la fenêtre générique |
| Textes courts | voir plus haut | Utilisés pour les toasts Android TV (coupés après deux lignes) et les notifications Kodi |
| Délais web / Android TV | 300 / 300 ms | Pour que notre message arrive après la fenêtre ou le toast d'erreur de l'application |
| Script de l'interface web | oui | Fermeture de la fenêtre générique, panneau des tâches et vue MediaID (désactivables séparément) |
| Nom de la vue par MediaID | `MediaID` | Texte affiché dans le menu des vues |
| Android TV : refermer le lecteur | oui (1,5 s) | Après un refus, envoie « Stop » à l'application pour revenir à la fiche au lieu de l'écran noir |
| Disque sur la fiche Android TV | oui | Ajoute la ligne du disque en tête du résumé ; trois textes modifiables (branché, absent, fichier introuvable), markdown accepté |
| Bouton du bandeau | oui, `Media` | Ajoute le bouton qui ouvre la vue par MediaID, et règle son nom |
| Tuile MediaID (accueil web) | oui | Ajoute la tuile dans « Mes médias » de la page d'accueil |
| Tuile MediaID (Android TV) | oui | Ajoute la bibliothèque virtuelle « MediaID » dans « Mes médias » de l'application Android TV |
| Lire les NFO non UTF‑8 en Windows‑1252 | oui | Lecteur « JellyUSB NFO », placé automatiquement avant « Nfo » |
| Réparer les textes à l'import | oui | Titres, titre original, résumé, slogan, genres, studios, tags, chapitres, pistes |
| Compléter depuis TheMovieDb | oui | Si ni le NFO ni le titre original ne donnent la lettre, la tâche demande le titre à TheMovieDb (par identifiant) et ne comble les « � » que s'il correspond partout ailleurs |
| Réparer après chaque scan | oui | Actif seulement après un premier lancement manuel de la tâche |

## Caractères accentués

- **Le symptôme :** des titres avec « � » à la place des lettres accentuées.
- **La cause habituelle :** des NFO enregistrés en Windows‑1252/Latin‑1 par d'anciens outils. Jellyfin
  les lit toujours en UTF‑8 et ignore la déclaration `encoding="ISO-8859-1"` (`BaseNfoParser.cs`).
- **La correction :**
  - à l'import, le lecteur « JellyUSB NFO » détecte un fichier qui n'est pas en UTF‑8 valide, le
    convertit en mémoire et le fait lire par l'analyseur NFO de Jellyfin lui-même ;
  - pour l'existant, la tâche relit le NFO quand le support est branché ; sinon, elle reprend les
    lettres du titre original, ou à défaut du titre TheMovieDb, quand il correspond au titre aux
    accents près ;
  - les textes doublement encodés (« Ã© », par exemple dans les chapitres des MKV) sont réparés en
    base.

⚠️ **Jellyfin peut réécrire les NFO sur les disques.** Si l'enregistreur de métadonnées « Nfo » est
activé dans la bibliothèque, Jellyfin réécrit lui-même les NFO dans les dossiers des films lors des
actualisations : c'est son comportement normal, pas celui du plugin. Pour que les disques ne soient
jamais modifiés, décochez « Nfo » dans *Bibliothèque > Enregistreurs de métadonnées*.

Un NFO déjà réécrit avec les « � » ne permet plus de retrouver la lettre. La tâche signale ces cas
dans le journal (« caractère illisible (�) non réparable automatiquement ») : ils se corrigent à la
main ou par une actualisation depuis TMDb.

## Comment ça marche (technique)

- **Interception de la lecture** : un middleware ASP.NET, inséré par `IStartupFilter`, intercepte :
  - `POST|GET /Items/{id}/PlaybackInfo`, que tous les clients appellent avant de lire ;
  - en filet de sécurité, `/Videos/{id}/stream*`, `master.m3u8`, `main.m3u8`, `hls*`,
    `/Items/{id}/Download` et `/Items/{id}/File`.

  Il ne répond qu'aux requêtes authentifiées, et seulement pour un élément que l'utilisateur a le
  droit de voir.
- **Présence du support** : lecture de `/proc/self/mountinfo`, sans jamais toucher un point
  d'automontage vide (ce qui coûterait environ 4 s puis `ENODEV`). Ensuite :
  - dossier absent ou vide : *support absent* ;
  - dossier monté mais fichier manquant : *fichier introuvable*.
- **Messages** : `ISessionManager.SendMessageCommand` (commande `DisplayMessage`) vers la session du
  client, que le plugin retrouve par son `DeviceId`. Le client est reconnu au champ `Client` de
  l'en-tête d'authentification.
- **Protection de la bibliothèque** : `ResilientFileSystem` remplace le système de fichiers de
  Jellyfin et passe toute lecture de dossier sous le chemin de base par `LibraryGuard`. Cette classe
  détermine l'état du support (table des montages, dossier vide ou pas) et connaît les dossiers de
  bibliothèque par les fichiers `*.mblink` de Jellyfin. Une liste est toujours lue jusqu'au bout avant
  d'être rendue.
- **Accents** :
  - `ILocalMetadataProvider<Movie|Episode>` « JellyUSB NFO » ;
  - événements `ItemAdded` / `ItemUpdated` ;
  - enregistrement en `ItemUpdateType.MetadataImport`, qui n'écrit rien sur les disques ;
  - tâche `IScheduledTask`.

- **Fiche Android TV** : `DetailsMediaIdMiddleware` repère `GET /Items/{id}` et
  `/Users/{u}/Items/{id}` venant de l'application Android TV (champ `Client` de l'authentification).
  Il ajoute la ligne en tête du champ `Overview` de la réponse JSON, que l'application affiche en
  markdown. Les caractères markdown et HTML d'un MediaID sont échappés.
- **Tuile Android TV** : `TvMediaViewMiddleware` ajoute la bibliothèque à `GET /UserViews`, et répond
  lui-même pour ses identifiants, dérivés du MediaID par MD5 : la fiche de la bibliothèque et de chaque
  disque, `GET /Items?ParentId=…` (les disques, puis les films d'un disque) et les images. Les fonds
  SVG sont convertis en PNG avec Svg.Skia, que Jellyfin fournit.
- **Vue MediaID** : le script ajoute une entrée au menu `#library-view-menu` de jellyfin-web. Il
  remplace le contenu de la page par sa vue quand l'adresse contient `jellyusb=media` (et
  `mediaId=…` pour la page d'un support), d'où l'historique du navigateur. Les données viennent de :
  - `GET /JellyUsb/Libraries/{id}/Media`, la liste des supports ;
  - `GET /JellyUsb/Libraries/{id}/Media/{MediaID}/Items`, les films d'un support, filtrés par
    dossier parent avec les droits de l'utilisateur.

## Tests

- `Jellyfin.Plugin.JellyUsb.Tests/` : 83 tests unitaires (`dotnet test`).
- `tests/integration/` :
  - `make-media.sh` crée une fausse médiathèque (NFO en Windows‑1252, chapitres doublement encodés) ;
  - `test-server.py` lance une **instance Jellyfin jetable** (port 18096, dossiers séparés, compte de
    test aléatoire) ;
  - `playback-checks.py` vérifie le blocage pour chaque famille de client ;
  - `unplug-checks.py` vérifie qu'un disque débranché ne fait retirer aucun film ;
  - `details-checks.py` vérifie la ligne du disque sur la fiche (Android TV seulement, rien en base) ;
  - `tvview-checks.py` vérifie la tuile MediaID de l'application Android TV (disques, films, images) ;
  - `run-all.sh` lance tout cela sur une instance et une médiathèque neuves, puis les supprime
    (environ 30 s). `release.sh` le lance avant chaque publication et s'arrête au moindre échec.

### Avant de mettre à jour Jellyfin

Le paquet Jellyfin de la machine sert aussi aux tests. Pour essayer une nouvelle version **avant** de
mettre le serveur à jour :

1. Télécharger l'archive Linux de la nouvelle version (repo.jellyfin.org) et l'extraire, par exemple
   dans `~/jellyfin-next`. On y trouve l'exécutable `jellyfin` et le dossier `jellyfin-web`.
2. Compiler JellyUSB avec les bibliothèques de cette version :
   ```bash
   dotnet build -c Release -p:JellyfinBinDir=$HOME/jellyfin-next/jellyfin
   ```
3. Lancer les tests sur cette version :
   ```bash
   JELLYUSB_JF_BIN=$HOME/jellyfin-next/jellyfin/jellyfin JELLYUSB_JF_WEBDIR=$HOME/jellyfin-next/jellyfin/jellyfin-web tests/integration/run-all.sh
   ```
4. Si tout passe et que la version change de numéro (12.4 vers 12.5), mettre à jour ensemble
   `TestedServerVersion` (`PluginServiceRegistrator.cs`), `targetAbi` (`release.sh`, `build.yaml`) et
   les paquets `Jellyfin.Controller` / `Jellyfin.Model`, puis publier une release. Mettre ensuite le
   serveur à jour.
5. Si un test échoue, ne pas mettre le serveur à jour tant que JellyUSB n'est pas corrigé.

Exemple :

```bash
python3 tests/integration/test-server.py start /tmp/jf-test /tmp/usb-test
```

## Licence

JellyUSB est distribué sous licence **GNU GPL v3** (voir [LICENSE](LICENSE)), comme le modèle
officiel de plugin Jellyfin (Jellyfin lui-même est sous GPL v2).
