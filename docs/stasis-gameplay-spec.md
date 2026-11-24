# Spécification gameplay — serveur « Stasis » (Dofus 3, semi-like, monocompte)

Source : wiki public play-stasis.net/wiki (lu le 24/11/2026, données du wiki vérifiées au 15/11/2026). Tout le texte est reformulé. Les chiffres sont repris tels quels. Les pages `play-stasis.net/` et `/join/` ne renvoient que leur titre (rendu JS) : « Serveur Dofus monocompte semi-like 3.0 ».

---

## 0. Principes généraux
- Serveur Dofus 3 « semi-like », jouable **solo ou en Duo** (2 persos de 2 comptes de jeu liés à un même compte Bubble, dans une seule fenêtre).
- Progression Dofus 3 jusqu'au **niveau 300**, puis Ascension (prestige à reset) et Évolution (mode hardcore).
- **Monde « figé »** : tout équipement droppé arrive **au jet max + bonus Stasis** (stasifié). Un objet acheté ne vaut jamais un objet farmé.
- **Pas de kamas** : la monnaie unique est l'**Orbe de Stasis**.
- Compte Bubble commun à plusieurs serveurs du réseau (portail play-bubble.net : inscription, connexion, ogrines). Launcher avec mise à jour automatique.
- **Vitesse de déplacement hors combat +25 %** pour tous les persos (cumulable avec dons de vitesse et la recherche Pas léger).
- Limite de groupe PvM : **4 combattants**, compagnons compris.
- Guides in-game : chaque système affiche un petit guide la première fois qu'on le rencontre ; tous relisibles dans le **Hub Stasis > onglet Guides**.
- Signalement de bug : **F11** au moment du problème.

## 1. Rates
| Élément | Valeur connue |
|---|---|
| XP de base | **Non publié.** Un exemple du wiki parle d'un « taux de 300 % » qui devient 550 % sous Marée (indice : XP de base ~x3, non confirmé) |
| Drop | Taux de base visibles dans la fenêtre Butins ; la PP au-delà de 100 augmente les chances, **300 PP = chances x2** |
| Max 1 item « de zone » par combat | + tirages séparés pour stuff de boss et pièces de panoplie |
| XP rattrapage après Ascension | x2 jusqu'au meilleur niveau atteint (max 300), jusqu'à x3 avec Rattrapage, multiplicateurs plafonnés à x9 |
| Hardcore | PV monstres x3, dégâts x2,25, drop x2 |
| Groupes recherche de donjon | +25 % (duo file publique) / +50 % (4, file publique) ; +15 % / +30 % si groupe préformé |
| Pénalité | Gains (Orbes) réduits si le perso est bien au-dessus du niveau des monstres (formule non publiée) |

## 2. Monnaie : Orbes de Stasis et économie
**Affichage** : en bas de l'inventaire, icône d'orbe à la place des kamas.

**Sources**
- Chaque combat PvM paie des Orbes ; plus le monstre est haut, plus ça paie. **Chaque participant reçoit une part complète** (pas de partage).
- Boss : nettement plus.
- Archimonstre dans le combat : **x5** sur tout le combat. Marée : **x2**.
- Missions Système, pistes, succès, bonus de donjon complet via la recherche de donjon.
- Raffineur (Sanctuaire) : rachat des items stasifiés en surplus (une « petite part » du prix Marchand) et des montures (10 % du coût d'accouplement).
- Vagues de la Fissure.

**Puits (sinks)**
| Puits | Usage |
|---|---|
| Marchand de Stasis | stuff non stasifié, clés de donjon, pierres d'âme, consommables |
| Comptoir de zone | buffs de farm 30 min |
| Fusion | +1 cran Stasis, prix selon niveau et cran |
| Dons | relance d'une offre (prix croissant avec le niveau) |
| Pension | accouplements (ex. gén. 7 : 4 000, 8 : 5 500, 9 : 7 500, 10 : 10 000) |
| Clés de Fissure | 1 800 / 4 650 / 7 950 |
| Fabrication | coût de service en Orbes |
| HDV du Sanctuaire | achat/vente entre joueurs en Orbes |

### Marchand de Stasis (Sanctuaire)
- Stock : équipement **non stasifié**, jets entre moyen et max ; une clé de donjon par tranche ; pierres d'âme, consommables.
- Deux onglets : **Conseillé** (stuff complet suggéré pour classe+niveau, ajout au panier en 1 clic) et **Catalogue** (tout le stock par palier, panoplies, filtres de stats ; clés et pierres en bas).
- Clés achetables aussi à distance depuis la fiche du donjon (fenêtre Donjons), même prix.

### Comptoir de zone
Bouton à droite de la barre de jauge. 4 buffs, valables **uniquement dans la sous-zone d'achat**, durée **30 min réelles** (le chrono tourne même déconnecté), racheter = prolonger (pas de cumul d'effet), prix selon niveau de la **zone**, achat impossible en combat. L'écran de fin de combat affiche le gain de chaque buff et le temps restant.
| Buff | Effet | Règles |
|---|---|---|
| Densité | +100 % drop, **ajouté** à la PP | exclusif avec Recrue |
| Recrue | un allié fantôme à 100 PP drope pour toi en fin de combat | moins cher que Densité ; exclusif avec Densité |
| Étude | +50 % XP de combat | refusé si une Marée booste déjà ton XP |
| Jauge | chaque monstre compte double dans la jauge | coûte **2x Densité** ; seulement en zone à jauge ; refusé sous Marée |
Le comptoir vend aussi le parchemin de reset pity (350 ogrines).

### Sanctuaire
Hub central partagé, accessible via **menu « + »** (à droite de la barre de raccourcis), le même bouton ramène au point d'origine ; bloqué en combat/action/carte anti-téléport. Contient : Marchand, Raffineur, raccourci banque, HDV, Gardien de la Fissure (boutique Jetons Event), menu Éveil, affichage du prochain événement.

### Bourse aux ogrines (boutique, onglet Bourse)
- Achat/vente immédiat au meilleur prix opposé, ou offre à son prix (reste ouverte hors ligne).
- **10 offres actives max**, **100 000 ogrines max par offre**, retrait à tout moment. Produit livré quand le perso est disponible. Cours et courbe de prix affichés.

## 3. Drop et jauge de zone
**Drop**
- Le butin d'une sous-zone est réparti entre ses monstres selon leur niveau. Monstres de panoplie → pièces de la panoplie ; chaque boss → son stuff ; chaque famille → ses ressources.
- Max **1 item de zone par combat**, + tirages séparés pour boss/panoplie. Équipement : jet max + stasifié.

**Jauge**
- Barre en haut de l'écran. Chaque victoire ajoute **tous les monstres du combat** ; en groupe, chaque joueur a sa propre jauge.
- Pleine → **1 item garanti** (stasifié) de la liste de la zone, remise à zéro, surplus perdu.
- Une jauge par sous-zone, et une par tranche de niveau pour les grandes sous-zones. Seules les sous-zones ayant une liste garantie ont une jauge.
- **Seuil** : base → 1,5x base → 2x base (plafond) après chaque item garanti. **Reset automatique toutes les 72 h**.
- **Parchemin de réinitialisation de la pity** : 350 ogrines ; reset immédiat des seuils de la zone actuelle pour ce perso ; cibles gardées ; progression conservée mais plafonnée à (nouveau seuil − 1) ; non consommé si déjà à la base.
- **Ciblage** : onglet Jauge, clic sur une ligne (re-clic = annuler), gratuit, persiste d'un item garanti au suivant, par sous-zone/tranche ; seulement les items de la tranche de butin de la carte courante (autres lignes grisées). Item retiré de la liste → la jauge pleine attend une nouvelle cible.
- Accélérateurs : Archimonstre x2, Marée x2 (cumulables, multiplicatifs), buff Jauge x2 (hors Marée). **N'accélèrent pas** : PP, Densité, Recrue, Étude, challenges.

**UI** : barre de zone (clic = fenêtre **Butins**), coffre à gauche (« ! » si aucune cible), bouton boutique à droite (comptoir). Butins a 2 onglets : **Drop** (portraits + taux de base avant PP/buffs) et **Jauge** (items garantis).

## 4. Archimonstres
- **Un seul par espèce sur tout le serveur**, remplace le monstre normal en tête de groupe dans sa sous-zone. Repop **~1 à 3 h** ; archis d'entraînement à Astrub et Madrestam : quelques minutes.
- Il suffit qu'il soit dans le groupe attaqué : **Orbes x5** (par joueur), **+400 % drop** (additif avec PP), **jauge x2**. Un archi invoqué en combat ne compte pas.
- Combat : groupe suit automatiquement, combat non fermable, préparation ≥ 10 s.
- **Caisse d'œufs** : rare à bas niveau, ~1/2 vers niv. 100, quasi sûre après 190 ; PP sans effet ; jamais sur archis d'entraînement.
- UI : Encyclopédie > onglet Archimonstres (en jeu + zone / repop imminent / temps restant, survol = surbrillance carte).

## 5. Stasification, fusion, Raffineur
Concerne équipements, trophées, prysmaradites (pas ressources, consommables, Dofus ordinaires, familiers, clés).
| Cran | Bonus sur jet max | Obtention |
|---|---|---|
| S1 | +25 % | drop normal |
| S2 | +50 % | 2 % des équipements ordinaires ; 30 % en hardcore (évolué) ; fusion |
| S3 | x2 | fusion ; 3 % en hardcore |
| S4 | x3 | fusion ; 1 % en hardcore |
- Drop S2 direct : notification « Cristallisation profonde ».
- Lignes **jamais multipliées** : PA, PM, PO, % CC, invocations, % résistances, dégâts d'arme, modificateurs de sorts.
- Répartition hardcore (perso évolué) : 66 / 30 / 3 / 1 %. Pas de jets aléatoires intra-cran en prod (jet parfait naturel du cran).

**Fusion** : 3 exemplaires identiques, même cran, dans le sac (non équipés, non verrouillés) → menu objet « Fusionner » → choisir l'exemplaire gardé, il monte d'un cran, les 2 autres sont consommés. Coût en Orbes affiché sur le bouton (croît avec niveau et cran).
- S1→S2 garde exos/FM ; S2→S3 efface exos/overs (repart du jet parfait S3) ; S3→S4 conserve les transcendances.
- S3/S4 équipables seulement par un perso **évolué** (lootables/échangeables avant) ; fusion vers S3/S4 refusée sans rien consommer.

**Forgemagie** : S1/S2 runes normales OK ; S3/S4 refusent runes/potions normales, acceptent runes de transcendance. **Orbe reconstituante** (S1–S4) : remet le jet parfait du cran, retire overs/exos/puits/verrou de transcendance, garde le cran ; puissance d'orbe selon le niveau de l'objet.
**Rune Do Sort cumulable** : +1 % dégâts de sort à 100 % de réussite, jusqu'à 4 % par pièce ; 250 Jetons Event ; bloquée par exo PA/PM/PO/invoc ; ne verrouille pas la FM ; conservée par l'orbe reconstituante ; en fusion, seul le bonus de l'objet gardé survit.

**Raffineur (Sanctuaire)** : rachète items stasifiés non voulus contre une petite part du prix Marchand, et les montures (10 % du coût d'accouplement de leur génération). Ne pas confondre avec le **Raffineur de cartes**.

## 6. Fabrication Stasis
- Accès raccourcis Stasis ou menu d'un objet. Ingrédients : objet de base **non stasifié** + ressources + Orbes + selon recette essence de gardien ou fragments de Fissure. Hors combat.
- **Devis** : possédé/manquant, achat direct de l'objet de base chez le Marchand ; liste triée par recettes réalisables.
- Résultat : **S1 jet parfait** ; recettes légendaires de Fissure : **S2 jet parfait**. La jauge ne réduit pas le coût.

## 7. Marées de Stasis
- Une sous-zone boostée par tranche de niveau ; cycle **50 min de Marée + 10 min de pause**, puis déplacement vers une autre zone.
- **+250 % XP** (additif au taux : 300 % → 550 %) si niveau dans la tranche (tolérance +20 niveaux au-dessus) ; **Orbes x2**, **jauge x2**, **repop immédiat** pour tous ; drop inchangé.
- Incompatibles : Étude, buff Jauge. Archi en Marée : bonus de jauge multipliés.
- UI : temps restant sur la barre de zone ; **Calendrier des Marées** dans le menu +.

## 8. Paliers 200-300 et accès zones
- Dès le niveau 200, l'XP est bloquée à chaque palier tant que tous les donjons du palier ne sont pas faits (ordre libre, kill du boss valable pour tout le groupe). L'XP au-delà est perdue, jamais retirée. Liste : Hub > onglet **Aperçu**.
| Palier | Nb | Donjons |
|---|---|---|
| 200 | 6 | Forgefroide de Missiz Frizz, Salons privés de Klime, Transporteur de Sylargh, Laboratoire de Nileza, Aquadôme de Merkator, Palais du roi Nidas |
| 225 | 6 | Vaisseau du Capitaine Meno, Défi du Chalœil, Temple de Koutoulou, Palais de Dantinéa, Brasserie du roi Dazak, Trône de la Cour Sombre |
| 250 | 7 | Chambre de Tal Kasha, Manoir des Katrepat, Belvédère d'Ilyzaelle, Mémoire d'Orukam, Souvenir d'Imagiro, Sanctuaire de Torkélonia, Breuil du Vénérable |
| 275 | 7 | Donjon du Comte Harebourg, Ventre de la Baleine, Arbre de Mort, Sentence de la Balance, Trône de Sang, Œil de Vortex, Fers de la Tyrannie |
| 300 | 9 | Tour de Bethel, Tour de Solar, Tempête de l'Eliocalypse, Rituel de Kabahal, Bataille de l'Aurore Pourpre, Chambre des maléfices, Temple du Dieu Kao, Caverne de Cire Momore, Autel de la Déchireuse |
- Le palier 300 ne bloque pas l'XP (300 = cap) mais ses 9 donjons sont requis pour l'Évolution.
- Monstres plus durs par palier : au palier 300, **PV x3**, jusqu'à **+4 PM** et **+200 Puissance** vs palier 200.
- Les **zones** d'un palier s'ouvrent jusqu'à **25 niveaux plus tôt** si les paliers précédents sont validés (zone 200 dès 175 ; zone 225 dès 200 si palier 200 fait). Ne s'applique pas aux donjons.
- Ascension remet les paliers à zéro ; perso évolué : validation uniquement en **hardcore**.

## 9. Ascension, Recherche, Évolution
### Ascension (dès niveau 50, Hub > onglet Ascension)
Éclats gagnés selon le niveau quitté : 50→1, 100→20, 150→64, 200→150, 225→400, 250→700, 275→1 050, 300→1 450.
- **Reset** : niveau, stats, dons/sorts du cycle, missions de route, paliers, succès ordinaires (sauf succès « serveur » archi/Fissure) ; stuff déséquipé ; retour carte de départ.
- **Conservé** : inventaire, banque, Orbes, métiers, items stasifiés, sorts spéciaux, panoplies enregistrées, classeur de cartes, Pension, Éclats, recherches, statut évolué.
- Choix : **Nouvelle run** (gratuite, nouveaux tirages) ou **Garder ma run** (500 ogrines : retrouve les dernières offres, relances comprises ; passif revient au niv. 50).
- Rattrapage XP : x2 jusqu'au meilleur niveau (≤300) ; voir Rattrapage/Élan ; plafond x9.

### Recherche (Hub > onglet Recherche, payée en Éclats, par personnage, non partagée au Duo)
Rangs ordinaires réaffectables gratuitement hors combat. **Métamorphose, Polyvalence, Évolution : permanentes, non remboursables.**
| Branche | Recherche | Coût par rang | Effet par rang |
|---|---|---|---|
| Préparation | Rattrapage | 20/40/60 | XP de rattrapage x2,25 / x2,5 / x3 |
| | Élan | 30/50 | +10 / +20 niveaux de rattrapage |
| | Pas léger | 10/20/30 | +5/+10/+15 % vitesse hors combat |
| | Tempo | 10/20/30 | animations de combat +5/+10/+15 % |
| | Mémoire de la Route | 40 | XP des missions x2 |
| Savoir | Relance | 40/80 | +1/+2 relances gratuites (offres ordinaires) |
| | Fortune | 30/50/70 | Rare 22/24/25 %, Légendaire 7/9/12 % |
| | Quatrième carte | 100 | 4e carte dans les offres de combat |
| | Seconde refonte | 50 | 2 refontes de stats gratuites par cycle |
| | Métamorphose | 500 | changer de classe au niv. 1 après Ascension |
| | Polyvalence | 500/500/500 | 2/3/4 configurations dons+sorts+équipement (passif partagé) |
| | Dons duo | 60/60/60 | carte à 2 stats dans 10/20/30 % des offres |
| Puissance | Bi-élément | 1 000 | 2e élément reçoit 60 % des dons de l'élément principal (hors équipement), choisi par config |
| | Résonance | 20/30/40/50/60 | +2/4/6/8/10 % aux stats des dons |
| | Endurance | 20/30/40 | +4/8/12 % PV max |
| | Ténacité | 20/30/40 | +1/2/3 % aux 5 résistances élémentaires |
| | Action | 150 | +1 PA |
| | Cadence | 500 | +1 lancer/tour (sorts limités par tour) |
| | Ciblage | 400 | +1 lancer/cible (sorts limités par cible) |
| | Évolution | 1 000 | Évolution permanente |
(Implicite : taux de base Fortune ≈ Rare 20 %, Légendaire 5 % — non confirmé.)

### Évolution
- Conditions : niveau 300, 1 000 Éclats disponibles, les 9 donjons du palier 300 faits depuis la dernière Ascension, aucun choix de don en attente.
- Bouton à maintenir (onglet Ascension). Coûte 1 000 Éclats et donne le gain d'Éclats d'une Ascension 300.
- Effet : retour **niveau 200**, statut évolué permanent ; dons ≤200 et passif conservés ; dons 225/250/275/300 à refaire.
- Débloque : port/fusion S3-S4, donjons et groupes **hardcore**, rang de prestige 1. Paliers à refaire en hardcore. Ascensions toujours possibles.

## 10. Missions et dons
### Le Système (HUD)
Une **mission de chapitre** + **2 pistes** affichées ; clic = Hub Stasis ; masqué en combat. Mission de chapitre : progression auto (victoires, kills, Orbes, niveaux, succès, œufs), une seule active, **GO** (téléporte à l'objectif / porte du donjon / ouvre la fenêtre), **Récupérer** ; récompenses Orbes + XP, bonus à la dernière mission du chapitre. Pistes : nouveau lot **tous les 10 niveaux**, XP par mission, bonus d'Orbes à la complétion d'une piste.

### Offres de dons
- 1 choix par niveau de 1 à 200, puis accomplissements aux niveaux **225, 250, 275, 300**.
- Offre standard : **3 cartes** (4 avec Quatrième carte), rareté par carte (Commun/Rare/Légendaire) ; 1re carte presque toujours une stat au choix ; dernière carte = don pas pris récemment ; si une offre de 5 n'a pas de légendaire, la suivante en garantit un. Ouverture auto après combat et à la reconnexion. Touches **1–5** valident directement.
- Relance : 1 par offre (combat/aventure/sort), coût en Orbes croissant avec le niveau ; Relance (recherche) ajoute des gratuites ; offres de départ et accomplissements non relançables.
- Hub > **Mes dons**.

### Dons d'aventure (tous les 5 niveaux, sauf niveaux de sorts) — jusqu'à 33 rangs, valeurs par rang
| Don | Commun | Rare | Légendaire |
|---|---|---|---|
| Sagesse | +3 | +4 | +6 |
| Prospection | +4 | +6 | +8 |
| Vitesse perso | +3 % | +4 % | +6 % |
| Vitesse d'animation | +4 % | +6 % | +8 % |
| Découverte | +1 Sag +2 PP | +2 Sag +3 PP | +3 Sag +4 PP |
| Élan | +1 % vit. +2 % anim. | +2 % +3 % | +3 % +4 % |

### Dons « Pension »
Niv. 20 : premier familier (choix parmi 4 Draomi) ; niv. 50 et 60 : œuf de dragodinde/muldo/volkorne gén. 1. Des cartes de don ajoutent des places d'incubation.

### Points de sort
- Offerts aux niveaux 10, 30, 50, 70, 90, 110, 130, 150, 170, 190 + les 4 accomplissements post-200 (14 points).
- Max **5 familles** (famille = sort + variante, points partagés), slots ouverts aux niveaux 10/30/50/70/90. Points max par famille : 1 (niv. 10), 2 (70), 3 (90), 4 (110), 5 (130). Offre de sort jusqu'à 5 familles. Ordre de palier imposé.
- **Types d'amélioration** : Économie (−1 PA), Enchaînement (+lancers/tour), Insistance (+lancers/cible), Précision (+10 à +30 points de CC par point selon niveau), Allonge (+PO), Adaptabilité (PO modifiable), Clairvoyance (sans LdV), Liberté (sans contrainte ligne/diagonale), Persistance (+cumul), Cadence (−1 tour de relance), Anticipation (−1 tour de délai initial). Caps typiques : 1 point avant 70, 2 dès 70, 3 dès 90. Certaines améliorations en exigent d'autres (ex. Insistance max exige Enchaînement 1).
- Catalogue : 44 sorts (22 familles) par classe ; possibilités : Crâ 252, Ecaflip 231, Eliotrope 237, Eniripsa 240, Enutrof 228, Féca 225, Forgelance 216, Huppermage 243, Iop 270, Osamodas 254, Ouginak 255, Pandawa 234, Roublard 227, Sacrieur 223, Sadida 197, Sram 237, Steamer 261, Xélor 246, Zobal 243.
- Exemples : Iop — Pression (Économie −1 PA cap 1 ; Précision jusqu'à 3 pts), Épée Divine (Enchaînement 1 puis 2 dès 70), Couperet (Clairvoyance + Liberté), Colère de Iop (Persistance, Cadence). Crâ — Flèche de Recul (Économie, Enchaînement), Flèche Harcelante (Adaptabilité, Précision), Balise de Survie (Allonge +1 puis +2), Sentinelle (Persistance, Anticipation). Eniripsa — Mot Tapageur (Allonge jusqu'à 3 pts dès 133), Mot Vampirique (Insistance 2 si Enchaînement 1), Mot d'Amitié (Enchaînement max si Cadence 1).
- Les améliorations d'une autre classe ne sont pas réutilisées en changeant de classe.

### Refontes (Mes dons)
| Refonte | Effet | Coût |
|---|---|---|
| Stats | retire les stats dans les possibilités de chaque carte | 1 gratuite/cycle (2 avec Seconde refonte), puis parchemin |
| Sorts | oublie les améliorations, re-propose leurs paliers | parchemin de refonte de sorts |
| Complète | refait tous les dons du cycle + nouveau choix de passif | parchemin de refonte complète |
| Passif | nouvelle offre de passif (+1 relance gratuite rendue, max 2) | parchemin d'oubli de passif |
S'applique à la configuration active (Polyvalence). Prix des parchemins en ogrines non publiés.

### Passifs de classe (choix au niv. 50)
3 passifs tirés parmi les 19 classes (y compris la sienne), 2 relances gratuites (3 nouveaux à chaque fois, 9 vus max), pas de relance payante. Actif auto en début de combat, **PvM uniquement**. Bouton « Choisir mon passif » dans Mes dons. Évolution conserve le passif.
| Classe | Passif (résumé) |
|---|---|
| Féca | fin de tour du bénéficiaire : bouclier 50 % PV max (1 t) pour lui et alliés alignés |
| Osamodas | chaque invocation/déclencheur : +5 % dmg/soins finaux 2 t, max 5/tour (25 %), soins cap 25 % |
| Enutrof | +3 % dmg finaux 2 t par PM de déplacement (cap 25 %/tour) ; garde jusqu'à 6 PM inutilisés |
| Sram | ennemi achevé : +5 % dmg 3 t + invisibilité 1 t (cassée en attaquant) |
| Xélor | tentative de retrait PA : +10 retrait PA 3 t et +5 % dmg 3 t (cap 25 %/tour) ; retrait réussi +1 PA 1 t (max 5) |
| Ecaflip | roulette début de tour (2 t, 2 cumuls) : +25 % dmg & +250 poussée / +50 % soins & +777 % CC / +25 % rés. mêlée+distance / +5 PO & +5 invoc / +3 PA / +3 PM ; CC : +50 puissance 2 t (x2) |
| Eniripsa | soin → +3 % dmg 2 t ; dégâts → +3 % soins 2 t |
| Iop | dégâts à ≤2 PO : +5 % dmg 2 t, max 6/tour (30 %) |
| Crâ | début de tour : +5 % dmg par ennemi en LdV, +2 PO par ennemi hors vue (1 t) |
| Sadida | tentative de retrait PM : +10 retrait PM 3 t (max 50), +5 % dmg 2 t (cap 25 %) ; PM retiré rend 1 PM (max 4/t) ; cible subit +5 % 1 t |
| Sacrieur | chaque allié survit à sa 1re attaque mortelle : 50 % PV, +25 % dmg 1 t, invulnérable jusqu'à son tour |
| Pandawa | +1 PA par ennemi au contact, +1 PM par ennemi à distance (max 4) ; ressources non utilisées : +5 % dmg (cap 15 % PA / 15 % PM), +2 % soins |
| Roublard | cycle 3 tours : +7 / +15 / +25 % dmg, au 3e les ennemis à ≤2 PO subissent +50 % |
| Zobal | tours impairs bouclier 25 % PV ; tours pairs +25 % dmg |
| Steamer | attirance/poussée/échange/dégât de poussée : +5 % dmg et +30 dmg poussée 2 t (cap 30 %/tour) |
| Eliotrope | téléport/échange d'un allié : bouclier 10 % PV 3 t (remplace) + 5 % dmg 2 t (cap 30 %) |
| Huppermage | sort élémentaire : +2 % carac meilleur élément 2 t (max 8 %) ; coup subi : +2 % rés. de l'élément (max 10 %) |
| Ouginak | +1 PM et +5 % dmg mêlée par case jusqu'à l'ennemi le plus proche (1 t) |
| Forgelance | invocation : ennemis autour +10 % dmg subis 1 t (4 cumuls) ; à sa mort +3 % dmg invocateur 3 t |

### Accomplissements 225-300
Un don aux niveaux 225, 250, 275, 300 (non relançable) + un point de sort chacun. Contenu exact des récompenses **non publié**.

## 11. Donjons
**Clés** (tranche = niveau du donjon) : [1-40], [41-80], [81-120], [121-160], [161-190], [191-200], [201-225], [226-250], [251-275], [276-300]. Une clé ouvre tous les donjons de sa tranche ; 1 clé par entrée ; certains donjons sans clé ; **aucune clé ne drope** (Marchand, fiche donjon, échange). Prix non publiés.
- **1 entrée gratuite par donjon et par perso, reset le mardi matin**, utilisée avant les clés.
- Reprise à la dernière salle après wipe : gratuite. Recommencer = nouvelle entrée payante.
- Chaque kill de boss donne l'« XP de succès » du donjon à chaque run.
- Groupe max 4. Options du chef dans la préparation (mémorisées par donjon) :
  - **Mécaniques Avec/Sans** (Sans dispo si tout le groupe a déjà battu le boss avec) : Sans retire invulnérabilités, plafonne les rés. ≥100 % à 50 %, PV x2 du monstre déprotégé, succès/challenges/idoles ne comptent pas ; immunités de contrôle conservées ; loot inchangé.
  - **Salle complète Oui/Non** : effectif « salle pleine » (≤4) même en petit groupe → plus d'XP/drop mais plus dur ; verrouillé au début du combat.
- **Recherche de donjon** : formats Solo (immédiat), Duo (2, +25 %), Équipe (4, +50 %) via file publique ; groupe préformé +15 %/+30 %. File sans limite de temps, on peut jouer en attendant, vérification de présence, téléport salle 1. Premiers départs solo du jour sans clé (nombre non publié) ; bonus d'Orbes pour donjon complet.
- **Hardcore** (évolués uniquement, tout le groupe) : uniquement via la recherche ; PV x3, dégâts x2,25, drop x2, crans 66/30/3/1 ; clés identiques ; compte pour les paliers. Mode hardcore aussi activable en monde ouvert par le chef (hors Fissure/Défis).
- Timers de donjon : aucun publié (hors Time Attack).

## 12. Fissure
PNJ Gardien de la Fissure (Sanctuaire). Expéditions à vagues, monstres plus forts à chaque vague, 4 combattants max, 1 perso par compte de jeu (Duo accepté).
| Expédition | Public | Clé/perso (Orbes) | Vagues pour valider | Limite |
|---|---|---|---|---|
| Surface | 150–200 | 1 800 | 1 | 5 |
| Profondeur | 225 | 4 650 | 3 | 15 |
| Abîme | 275–300 | 7 950 | 5 | aucune |
Bonus de formation : 1 combattant +4 PA +3 PM +25 % PV +10 % dmg ; 2 : +3/+2/+15 %/+8 % ; 3 : +2/+1/+8 %/+5 % ; 4 : rien.
Récompenses : Orbes + XP par vague ; fragments selon vagues et validation ; légendaires en Profondeur/Abîme ; **Dofus garanti après 1 000 vagues éligibles** (vagues ≥5 en Abîme). Abandon possible par le dernier survivant (garde les vagues faites) ; abandonner avec un allié vivant = pénalité.

## 13. Dofus et Éveil
Taux de base fixes (tous grades), boostés par PP. Exemples : Argenté 1 % (Bouftou Royal, Mob l'Éponge, Tournesol Affamé) ; Cawotte 0,5 % (Wa Wabbit, Wa Wobot) ; Pourpre Minotot 0,1 %, Minotoror 0,05 % ; Émeraude Koulosse 0,3 %, Meulou 0,1 % ; Vulbis Torkélonia 0,3 %, Crocabulia 0,08 % ; Glaces Harebourg 0,5 %, Klime/Frizz/Nileza/Sylargh 0,3 % ; Ébène Ombre 0,2 % ; la plupart des Dofus 180 à 0,3 % par boss. (Table complète de 25 Dofus sur /wiki/farm/dofus/.)
**Éveil Partie I** (Partie II et Réunification fermées) : sacrifice de copies → fragment → parcours de donjons **en hardcore** (seules les victoires après le début comptent) → défi final (solo ou Duo, sans compagnon).
| Dofus | Niveau | Copies | Adversaire final |
|---|---|---|---|
| Cawotte | 225 | 25 | Gwand Viziw Wabbit |
| Glaces | 250 | 7 | Jacquemart T. de Harebourg |
| Émeraude | 250 | 7 | Oracle d'Aerafal |
| Pourpre | 275 | 8 | Ignemikal |
| Dolmanax | 275 | 7 | Thalos-Kyarrelmne (phases immunes Terre, Feu, Air, Eau) |
| Vulbis | 300 | 6 | Dragon sans nom |
Parcours : Cawotte (Château et Terrier du Wa Wabbit, Palais du roi Nidas, Temple de Koutoulou, Manoir des Katrepat) ; Glaces (Kolosso, Antichambre des Gloursons, Forgefroide, Klime, Sylargh, Nileza, Harebourg) ; Émeraude (Meulou, Koulosse, Tal Kasha, Ilyzaelle, Ventre de la Baleine) ; Pourpre (Fabrique de feux d'artifice, Labyrinthe du Minotoror, Salle du Minotot, Trône de Sang, Tour de Solar, Aurore Pourpre) ; Dolmanax (Clos des Blops, Chambre des maléfices, Blop Multicolore Royal, Dantinéa, Imagiro, Sentence de la Balance, Œil de Vortex, Arbre de Mort) ; Vulbis (Crocabulia, Torkélonia, Fers de la Tyrannie, Rituel de Kabahal, Dieu Kao).
Dofus stasifiés (échangeables, hors crans S1-S4, non cumulables avec la version normale) — résumé : Cawotte 200 Sag + 60 esq. PA/PM, 1er retrait par tour annulé ; Glaces +100 dmg 5 éléments, 8 % rés., +2 % dmg finaux par tour immobile (4 cumuls) ; Émeraude 800 Vita, 10 % rés. mêlée, bouclier 250 % du niveau par ennemi au contact ; Pourpre 275 Puissance, +2 % dmg par attaque subie 3 t (12 cumuls) ; Dolmanax 150 dans 4 caracs + 25 dmg, +10 dmg tous éléments par élément frappé (5 cumuls) ; Vulbis 1 PM 1 PO, +20 % dmg si aucun dégât subi, sinon +60 tacle +10 %.

## 14. Cartes de monstres
Classeur par compte de jeu (Duo = classeur séparé), bonus de collection (famille complète) pour tous les persos du compte, conservés à l'Ascension. Paquets gagnés en jouant, ouverts depuis l'inventaire ; carte collée = consommée. Doublons : prix HDV, filtre « Manquantes », vue Échanges, liste publique de recherches. **Raffineur de cartes** : mise de 1 à 10 cartes pour tenter une rareté supérieure, chance max Rare 75 %, Épique 45 %, Légendaire 25 % (selon valeur du lot), échec = lot perdu. Paquets saisonniers (Nowel, Pwâk, Vulkania, Potager d'Halouine) ciblables dans la jauge.

## 15. Pension (montures, familiers)
Menu + ; onglets Incubation, Animaux, Étude, Accouplement, Guide ; partagée par compte de jeu.
- Places d'incubation : 2 au départ, +1 quand un perso du compte atteint 100, +2 achetables 300 ogrines chacune, + cartes de don ; **max 8**.
- Incubation en temps réel (même déco) 5 min–1 h ; accélération **20 ogrines = −15 min** ; « Retirer » rend l'œuf mais perd le temps.
- Salle d'étude : 3 places, 5 % de l'XP de chaque combat gagné par le compte (x2 si animal < moitié du niveau du perso), rien en AFK ; animal équipé +25 % de l'XP du combat en bonus. Stats ~30 % du max au niv. 1 → 100 % au niv. 300.
- Caisse d'œufs : 70 % œuf de monture gén. 1, 30 % œuf mystère familier/montilier rare ou légendaire. Aucun PNJ ne vend d'œufs. Tout s'échange sauf les œufs de dons.
- **Montures** (dragodinde, muldo, volkorne ; 10 générations ; montables niv. 60 dans la case familier, exclusives avec familier) : accouplement même espèce + même génération = **toujours** un œuf de la génération suivante (seul le skin est aléatoire) ; coût Orbes + 1 portée par parent ; 1 couple à la fois par compte ; 1 min–1 h. Portées : gén. 1–3 : 3, 4–7 : 2, 8–9 : 1, 10 : stérile. Coûts : gén. 7 4 000, 8 5 500, 9 7 500, 10 10 000 Orbes.
- **Familiers** : Starter 5 min (don 20), Commun 10 min, Rare 30 min, Légendaire 1 h ; œuf mystère : espèce tirée à l'éclosion, 5 % de monter d'une rareté (enchaînable). Montilier = familier à apparence de monture.

## 16. Événements
Rotation quotidienne Time Attack → Poutch → Grande Traque (chaque événement tous les 3 jours), heure de Paris, affichés dans le Hub (classement).
| Événement | Durée | Horaire | Règles | Récompenses (Jetons Event, 1er/2e/3e) |
|---|---|---|---|---|
| Time Attack | 1 h | 18h semaine, 21h week-end | solo strict, meilleur temps, entrée gratuite. Cat. I perso 110–200 / donjon 130–180 normal ; II 201–275 / 200–250 normal ; III 250–300 évolué / 251–300 hardcore | I 25/15/10 ; II 50/30/20 ; III 100/75/50 |
| Concours Poutch | 1 h | tiré entre 13h et 21h | max de dégâts sur le Poutch en 1, 2 ou 3 tours ; solo ; coef. ×1,25 Féca/Eniripsa/Zobal, ×0,8 Iop/Roublard | I non évolué 110–200 15/10/5 ; II non évolué 201–300 25/15/10 ; III évolué 40/30/20 |
| Grande Traque | 30 min | tiré entre 13h et 21h | trouver 50–100 PNJ cachés, indices de zone, 1 point par 1re découverte | 75/50/25 |
**Boutique du Gardien de la Fissure** : seul article publié : Rune Do Sort cumulable, 250 jetons.

## 17. Métiers
Les métiers existent (conservés à l'Ascension) mais **aucune règle spécifique** n'est publiée ; la « Fabrication Stasis » (§6) est le système de craft maison.

## 18. PvP
Rien de publié, hormis : passifs de classe désactivés en PvP.

## 19. Boutique / ogrines
Ogrines gagnées en jouant/votant ou achetées via Bubble ; échangeables contre Orbes à la Bourse. Usages : cosmétiques et confort uniquement — refontes de dons, places de Pension (300), accélération (20 = 15 min), reset pity (350), Garder ma run (500). Prix affichés avant confirmation.

## 20. Duo
- Ajout via fenêtre **Duo** > « Ajouter un héros » (autre compte de jeu, ou création), hors combat, carte accessible aux deux ; l'autre compte doit être déconnecté ailleurs ; en donjon, former avant d'entrer.
- Carte : le duo suit le perso contrôlé ; clic portrait = inventaire/panneaux. Combat : contrôle du perso dont c'est le tour. Auto-pass par perso. « Retirer » sort le perso.
- Personnel : dons, recherches, Éclats, Évolution, succès, jauges, cibles, inventaire, Orbes ; Pension et classeur par compte.
- « Échanger » (hors combat) : objets + Orbes. Invitation = les deux ; exclusion = les deux ; limite 4.
- Clés : prises dans l'un ou l'autre inventaire, 2 entrées payantes = 2 clés ; « Acheter pour le Duo » : l'autre paie d'abord, sinon le contrôlé paie tout.
- Fissure et Défis d'Éveil OK ; Time Attack et Poutch interdits.

## 21. QoL
+25 % vitesse hors combat ; Pas léger, Tempo, dons de vitesse ; GO téléport ; Sanctuaire en 1 clic aller-retour ; banque au Sanctuaire ; achat de clés à distance ; touches 1–5 ; guides auto ; F11 ; fin de combat récapitulant les buffs.

## 22. Parcours nouveau joueur
1. Compte Bubble → launcher → création perso (19 classes).
2. Don dès le niveau 1 ; le Système guide de zone en zone (GO / Récupérer) ; guides contextuels.
3. Farm : jauge de zone, cibler un item (coffre « ! »), Orbes ; archis d'entraînement à **Astrub** et **Madrestam** (repop rapide). Point de départ exact non publié (carte de départ standard probable).
4. Marchand (Conseillé) pour combler, puis drops/jauge/fusion ; donjons avec entrée gratuite hebdo.
5. Niv. 20 familier, 50 passif + monture, 60 monture équipable ; pistes tous les 10 niveaux.
6. 200+ : paliers de donjons ; 300 : Évolution ; Ascension dès 50 pour les Éclats.

## Ce qui manque / ambigu
| Sujet | Problème |
|---|---|
| Taux d'XP/drop/kamas de base | Non publiés (exemple « 300 % » dans la page Marées seulement) |
| Montants d'Orbes | Gains par combat/boss/mission, formule de pénalité de niveau, prix Marchand/clés/fusion/buffs/relances inconnus |
| Seuils de jauge | Valeur de base par zone non publiée (seulement x1 / x1,5 / x2) |
| Raffineur | « Petite part » du prix Marchand non chiffrée |
| HDV | Mentionné (Sanctuaire, Orbes), aucune règle (taxes, lots) |
| Rareté des dons | Taux de base Commun/Rare/Légendaire non publiés (Fortune laisse supposer 20 %/5 %) |
| Liste des dons de combat/standard | Seuls les dons d'aventure sont détaillés |
| Accomplissements 225-300 | Contenu des récompenses non publié |
| Recherche de donjon | Nombre de départs gratuits/jour, montant du bonus d'Orbes, délai de confirmation inconnus |
| Hardcore monde ouvert | Récompenses exactes non précisées |
| Fissure | Quantités de fragments, paliers de récompenses, pénalité d'abandon non chiffrés |
| Boutique Jetons Event | Un seul article publié |
| Métiers / PvP | Aucune page dédiée |
| Point de départ / tutoriel | Carte de départ et premier tutoriel non décrits |
| Prix ogrines | Parchemins de refonte et cosmétiques non chiffrés |
| Pages d'accueil et /join/ | Contenu rendu en JS, non lisible (titre seul) |
| Prestige | « Rang de prestige 1 » mentionné sans effets |
