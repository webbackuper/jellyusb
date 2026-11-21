# jellyusb — montage automatique des disques USB `MOVIESnn` pour Jellyfin (côté OS)

Ce dossier contient la partie « système » : une règle udev, une unité systemd
modèle et un script d'aide qui montent automatiquement tout disque USB dont le
**label** du système de fichiers est `MOVIES00` … `MOVIES99` sur
`/mnt/usb/<LABEL>`, et qui **suppriment le dossier** quand le disque est retiré.

| Fichier | Installé dans | Rôle |
|---|---|---|
| `99-jellyusb.rules` | `/etc/udev/rules.d/` | détecte le disque, démarre `jellyusb-mount@<LABEL>.service` |
| `jellyusb-mount@.service` | `/etc/systemd/system/` | unité liée (`BindsTo=`) au périphérique `/dev/disk/by-label/<LABEL>` |
| `jellyusb-mount` | `/usr/local/sbin/` | `mount` / `umount` / `status` (détection du FS, options, journal) |
| `jellyusb-mount.default` | `/etc/default/jellyusb-mount` | configuration (UID/GID, umask, pilote NTFS…) |
| `install-os.sh` | — | installation / désinstallation (à lancer avec `sudo`) |

## Pourquoi

* **Jellyfin conserve les éléments d'une bibliothèque quand le dossier racine
  est absent** : si `/mnt/usb/MOVIES03` n'existe pas, les films du disque 03
  restent dans la bibliothèque (marqués indisponibles) au lieu d'être supprimés
  au prochain scan.
* Un dossier **vide** est aujourd'hui (Jellyfin 12.4) traité de la même façon,
  mais une modification en discussion chez Jellyfin (PR #17618, « Treat empty
  library roots as accessible ») ferait alors supprimer les films : un dossier
  absent est le seul cas sûr à long terme. Et avec l'ancien montage
  `x-systemd.automount` de `/etc/fstab`, un point d'automontage dont le disque
  est absent répond `ENODEV` (« No such device ») : cette erreur faisait échouer
  **tous** les scans de bibliothèque, pas seulement celui du disque absent.
* D'où la règle : **disque absent ⇒ `/mnt/usb/<LABEL>` n'existe pas**. Pas
  d'automount, pas de dossiers vides pré-créés. `/mnt/usb` lui-même existe
  toujours.

## Fonctionnement

1. Branchement : udev voit une partition (ou un disque sans table de
   partitions) avec `ID_FS_USAGE=filesystem` et `ID_FS_LABEL=MOVIES[0-9][0-9]`,
   et ajoute `SYSTEMD_WANTS=jellyusb-mount@<LABEL>.service` (uniquement sur
   l'événement `add`). La règle pose aussi `UDISKS_IGNORE=1` pour que le
   bureau (GNOME/udisks2) ne monte pas le disque ailleurs.
2. L'unité exécute `jellyusb-mount mount <LABEL>` : validation du label,
   détection du type (`blkid -o value -s TYPE`), `mkdir /mnt/usb/<LABEL>`,
   montage avec les options adaptées. En cas d'échec, le dossier est supprimé.
3. Retrait (ou `systemctl stop`) : `BindsTo=` arrête l'unité, qui exécute
   `jellyusb-mount umount <LABEL>` : `sync` (si le disque est encore là),
   `umount` (repli sur `umount -l` si occupé ou si le disque a disparu),
   puis `rmdir` du dossier **s'il est vide**.
4. Démarrage de la machine : les disques déjà branchés sont montés avant le
   démarrage de `jellyfin.service` ; à l'arrêt, Jellyfin est arrêté avant le
   démontage.

Tous les messages vont dans le journal avec l'étiquette `jellyusb-mount`.

## Installation

```sh
cd os
./install-os.sh --dry-run        # facultatif : affiche ce qui sera fait
sudo ./install-os.sh
```

Le script copie les fichiers, crée `/etc/default/jellyusb-mount` (s'il
n'existe pas déjà), lance `udevadm control --reload` et
`systemctl daemon-reload`, puis **affiche** (sans les appliquer) les étapes de
migration ci-dessous.

Désinstallation : `sudo ./install-os.sh --uninstall` (ajouter `--purge` pour
supprimer aussi la configuration). Les unités actives sont arrêtées (donc les
disques démontés) avant la suppression.

### Configuration (`/etc/default/jellyusb-mount`)

| Variable | Défaut | Rôle |
|---|---|---|
| `OWNER_UID` / `OWNER_GID` | `1000` / `1000` | propriétaire des fichiers (ntfs, exfat, vfat) |
| `UMASK` | `022` | fichiers 644, dossiers 755 : lisibles par `jellyfin` |
| `NTFS_DRIVER` | `ntfs-3g` | `ntfs-3g` (FUSE) ou `ntfs3` (pilote noyau) |
| `NTFS_RO_FALLBACK` | `yes` | NTFS « sale »/hiberné : remonter en lecture seule |
| `BASE` | `/mnt/usb` | dossier parent |
| `LABEL_REGEX` | `^MOVIES[0-9]{2}$` | labels acceptés (à garder cohérent avec la règle udev) |
| `JELLYFIN_USER` | `jellyfin` | utilisateur testé après montage (droits de lecture) |
| `SYNC_TIMEOUT` | `30` | délai max du `sync` avant démontage |

Le fichier doit appartenir à root et ne pas être modifiable par le groupe ou
les autres (sinon le script refuse de le lire).

## Migration depuis `/etc/fstab`

Les lignes d'automontage existantes (par exemple
`ntfs-3g defaults,nofail,x-systemd.automount,…` pour `/mnt/usb/MOVIES00`)
doivent disparaître, sinon l'automount occupe `/mnt/usb/MOVIES00` et le
nouveau script refuse de monter par-dessus.

```sh
# 1. sauvegarde puis mise en commentaire des lignes /mnt/usb/MOVIESnn
sudo cp -a /etc/fstab /etc/fstab.bak-jellyusb
sudo sed -i -E 's|^([^#].*[[:space:]]/mnt/usb/MOVIES[0-9][0-9][[:space:]].*)$|# jellyusb: \1|' /etc/fstab
grep -n '/mnt/usb/' /etc/fstab

# 2. arrêt des automounts (et des montages s'ils sont actifs)
sudo systemctl stop 'mnt-usb-MOVIES*.automount'
sudo systemctl stop 'mnt-usb-MOVIES*.mount'

# 3. systemd oublie les unités générées depuis fstab
sudo systemctl daemon-reload
findmnt -R /mnt/usb          # plus rien ne doit être monté sous /mnt/usb

# 4. suppression des dossiers vides MOVIES00..MOVIES99 (rmdir refuse les non vides)
for d in /mnt/usb/MOVIES[0-9][0-9]; do
    [ -d "$d" ] && ! mountpoint -q "$d" && sudo rmdir -v -- "$d"
done

# 5. monter les disques déjà branchés (ou les débrancher/rebrancher)
sudo systemctl start jellyusb-mount@MOVIES00
sudo jellyusb-mount status
```

Les chemins vus par Jellyfin (`/mnt/usb/MOVIES00`, …) ne changent pas : rien à
modifier dans les bibliothèques.

## Éjecter un disque proprement

```sh
sudo systemctl stop jellyusb-mount@MOVIES03
# ou, équivalent (délègue à systemctl) :
sudo jellyusb-mount umount MOVIES03
```

Attendre la fin de la commande, vérifier que `/mnt/usb/MOVIES03` a disparu,
puis débrancher. Évitez d'éjecter pendant une lecture ou un scan : si un
fichier est ouvert, le démontage passe en mode paresseux (`umount -l`).

Pour remonter sans débrancher : `sudo systemctl start jellyusb-mount@MOVIES03`.
(Un disque éjecté avec `systemctl stop` puis laissé branché n'est **pas**
remonté automatiquement : c'est voulu.)

État général : `sudo jellyusb-mount status`.

Un arrachage sans éjection est géré (démontage paresseux + `rmdir`), mais
risque de laisser un NTFS « sale » (voir ci-dessous).

## Notes par système de fichiers

Options appliquées (toutes avec `nosuid,nodev,noatime`) :

| Type (`blkid`) | Montage | Options |
|---|---|---|
| `ntfs` | `ntfs-3g` (défaut) | `uid=1000,gid=1000,umask=022` |
| `ntfs` | `ntfs3` (si `NTFS_DRIVER=ntfs3`) | `uid=1000,gid=1000,umask=022,iocharset=utf8` |
| `exfat` | `exfat` | `uid=1000,gid=1000,umask=022,iocharset=utf8` |
| `vfat` | `vfat` | `uid=1000,gid=1000,umask=022,utf8,shortname=mixed,flush` |
| `ext2/3/4`, `xfs`, `btrfs` | natif | `defaults,noatime` (pas d'uid/gid) |
| autre (BitLocker, hfsplus, iso9660…) | — | refusé, message dans le journal |

### NTFS

* `ntfs-3g` est conservé par défaut (comme aujourd'hui). Les noms de fichiers
  sont toujours convertis en UTF-8 (l'unité définit en plus `LANG=C.UTF-8`).
* Alternative : `NTFS_DRIVER=ntfs3` (pilote noyau, plus rapide, moins de CPU).
* **Volume « sale » ou Windows en hibernation / démarrage rapide** :
  ntfs-3g refuse le montage en lecture-écriture (« Windows is hibernated,
  refused to mount » ou « unclean file system »). Avec `NTFS_RO_FALLBACK=yes`
  (défaut), le script **remonte en lecture seule** et l'écrit dans le journal :
  Jellyfin peut lire, rien n'est modifié sur le disque. Le script ne lance
  **jamais** de réparation automatique.
  Pour corriger :
  1. **Recommandé** : brancher le disque sur Windows, désactiver le
     « démarrage rapide », faire un arrêt complet (ou `chkdsk /f X:`).
  2. `sudo ntfsfix -d /dev/sdX1` : efface le journal NTFS et le marqueur
     « sale ». Réparation minimale, **peut perdre les dernières écritures**
     faites sous Windows.
  3. Option ntfs-3g `remove_hiberfile` (montage manuel) : **supprime
     l'hibernation Windows**, toute session Windows en veille et les données
     non écrites sont perdues.
  Disque démonté avant toute réparation (`sudo systemctl stop jellyusb-mount@MOVIESnn`).

### ext4, xfs, btrfs

Pas d'options `uid/gid` : les droits viennent du système de fichiers. Les
fichiers doivent être lisibles par l'utilisateur `jellyfin` (uid 112), sinon
ils sont invisibles pour Jellyfin. Après montage, le script vérifie que
`jellyfin` peut lire la racine et écrit un avertissement sinon (il ne vérifie
pas chaque fichier). Solutions, disque monté :

```sh
sudo chmod -R a+rX /mnt/usb/MOVIES05                    # tout lisible par tous
# ou une ACL ciblée (héritée par les nouveaux fichiers) :
sudo setfacl -R -m u:jellyfin:rX -m d:u:jellyfin:rX /mnt/usb/MOVIES05
```

Pour copier des films sur le disque en gardant ces droits, utiliser un umask
022 (le défaut).

### exFAT et FAT32 (vfat)

* `iocharset=utf8` (exFAT) / `utf8` (vfat) : les accents et caractères non
  ASCII des noms de fichiers sont corrects. Sans cela, vfat utiliserait
  `iso8859-1` et les noms seraient abîmés.
* `flush` (vfat) : écritures vidées plus tôt, moins de pertes en cas
  d'arrachage.
* FAT32 : fichiers limités à 4 Go — préférer exFAT ou NTFS pour des films.

## Donner un label à un disque

Disque **démonté** (`sudo systemctl stop jellyusb-mount@…` ou pas encore monté) :

| FS | Commande |
|---|---|
| NTFS | `sudo ntfslabel /dev/sdX1 MOVIES03` |
| exFAT | `sudo exfatlabel /dev/sdX1 MOVIES03` |
| FAT32 | `sudo fatlabel /dev/sdX1 MOVIES03` |
| ext4 | `sudo e2label /dev/sdX1 MOVIES03` |
| XFS | `sudo xfs_admin -L MOVIES03 /dev/sdX1` (paquet `xfsprogs`) |
| btrfs | `sudo btrfs filesystem label /dev/sdX1 MOVIES03` |

Le label doit être exactement `MOVIES` + deux chiffres, en majuscules.
Ensuite débrancher/rebrancher le disque (ou `sudo systemctl start jellyusb-mount@MOVIES03`).

> **Attention** : le label est l'identifiant du média (MediaID) et donne le
> chemin `/mnt/usb/<LABEL>`. **Changer le label d'un disque déjà indexé change
> ses chemins dans Jellyfin** : ses films seront vus comme disparus et
> réindexés comme nouveaux (perte de l'historique de lecture, des métadonnées
> modifiées à la main, etc.). Ne réétiquetez pas un disque existant, et ne
> donnez jamais le même label à deux disques (un seul serait monté).

## Ajouter un nouveau disque (MediaID) à la bibliothèque Jellyfin

1. Étiqueter le disque (ex. `MOVIES04`), le **brancher** et vérifier qu'il est
   monté : `sudo jellyusb-mount status` ou `ls /mnt/usb/MOVIES04`.
   Le disque doit être branché : sinon le dossier n'existe pas et ne peut pas
   être choisi dans le tableau de bord.
2. Jellyfin → Tableau de bord → Médiathèques → (la bibliothèque) → Gérer la
   bibliothèque → Dossiers → **+** → choisir `/mnt/usb/MOVIES04` → OK.
3. Lancer « Actualiser les métadonnées » / « Scanner toutes les
   médiathèques ».

Une fois ajouté, le disque peut être débranché : le dossier disparaît et
Jellyfin garde ses éléments jusqu'au prochain branchement.

## Dépannage

```sh
journalctl -t jellyusb-mount -b                 # messages du script
journalctl -u 'jellyusb-mount@*' -b             # unités systemd
systemctl status jellyusb-mount@MOVIES03
sudo jellyusb-mount status                      # dossiers, montages, disques présents
udevadm monitor --udev --property --subsystem-match=block   # puis brancher le disque
udevadm info /dev/sdX1                          # ID_FS_LABEL, ID_FS_TYPE, ID_FS_USAGE, SYSTEMD_WANTS
ls -l /dev/disk/by-label/
systemctl list-units --all 'dev-disk-by*label-MOVIES*'
```

Problèmes fréquents :

* **Rien ne se passe au branchement** : `udevadm info /dev/sdX1` doit montrer
  `ID_FS_LABEL=MOVIESnn`, `ID_FS_USAGE=filesystem` et
  `SYSTEMD_WANTS=jellyusb-mount@MOVIESnn.service`. Label en minuscules, avec
  espace ou 3 chiffres ⇒ ignoré. Règle modifiée ⇒
  `sudo udevadm control --reload` puis rebrancher.
* **« already a mount point, refusing »** : une ancienne entrée fstab /
  automount occupe encore le dossier (voir Migration).
* **« exists and is not empty, refusing »** : des fichiers ont été écrits dans
  `/mnt/usb/<LABEL>` alors que le disque était absent. Les déplacer, supprimer
  le dossier, rebrancher.
* **« is already mounted on /media/… »** : le bureau a monté le disque avant
  nous ; le démonter (`udisksctl unmount -b /dev/sdX1`) puis
  `sudo systemctl start jellyusb-mount@<LABEL>`.
* **Monté en lecture seule** : NTFS sale/hiberné, voir plus haut.
* **Jellyfin ne voit pas les fichiers (ext4/xfs/btrfs)** : droits, voir plus haut.

## Test avec une image disque (loop)

Sans disque USB, avec une image de 64 Mo et le label `MOVIES98` :

```sh
cd /tmp
truncate -s 64M movies98.img

# un seul de ces formatages :
mkfs.ntfs  -F -Q -L MOVIES98 movies98.img     # NTFS (ntfs-3g)
mkfs.exfat -L MOVIES98 movies98.img           # exFAT
mkfs.ext4  -L MOVIES98 movies98.img           # ext4
mkfs.xfs   -L MOVIES98 movies98.img           # XFS (paquet xfsprogs ; image >= 300 Mo exigée par les versions récentes)

LOOP=$(sudo losetup -fP --show movies98.img)  # ex. /dev/loop40
udevadm info "$LOOP" | grep -E 'ID_FS_(LABEL|TYPE|USAGE)|SYSTEMD_WANTS'
ls -l /dev/disk/by-label/MOVIES98
```

Remarques sur les périphériques loop :

* `losetup` réutilise un périphérique loop existant : udev reçoit un
  événement **`change`**, pas `add`. Or la règle ne démarre l'unité que sur
  `add` (pour ne jamais remonter un disque éjecté). Il faut donc simuler le
  branchement :

  ```sh
  sudo udevadm trigger --action=add --settle "$LOOP"
  systemctl status jellyusb-mount@MOVIES98
  ls /mnt/usb/MOVIES98 && sudo jellyusb-mount status
  ```

* Selon le cas, le lien `/dev/disk/by-label/MOVIES98` peut tarder ou manquer
  (sonde `blkid` pas encore relancée) : `sudo udevadm trigger --action=change
  --settle "$LOOP"` puis l'`add` ci-dessus. On peut aussi tester le script
  seul : `sudo systemctl start jellyusb-mount@MOVIES98`.
* L'image formatée sans table de partitions est un « superfloppy » : c'est
  `/dev/loopN` lui-même qui porte le label (avec `-P` et une table de
  partitions, ce serait `/dev/loopNp1`).

Tests à faire :

```sh
# éjection propre : le dossier doit disparaître
sudo systemctl stop jellyusb-mount@MOVIES98 ; ls -d /mnt/usb/MOVIES98   # -> absent

# retrait brutal : remonter, puis détacher le loop pendant que c'est monté
sudo systemctl start jellyusb-mount@MOVIES98
sudo losetup -d "$LOOP"          # le montage garde une référence : le loop se détache au démontage
sudo udevadm trigger --action=remove --settle "$LOOP"   # simule la disparition du périphérique
journalctl -t jellyusb-mount -n 20
ls -d /mnt/usb/MOVIES98          # -> absent

# ext4 : vérifier l'avertissement de droits
#   (créer un dossier en 700 sur l'image, remonter, lire le journal)

# nettoyage
sudo losetup -d "$LOOP" 2>/dev/null; rm -f /tmp/movies98.img
```

`losetup -d` sur un loop monté ne libère le périphérique qu'au démontage ;
l'événement `remove` simulé fait tomber l'unité `dev-disk-by\x2dlabel-MOVIES98.device`
et donc, via `BindsTo=`, déclenche le démontage. Un vrai test de retrait
brutal reste de débrancher une clé USB réelle.
