# Audit des écarts Stasis ↔ JondoEmu

Date : 25/11/2026. Référence : `docs/stasis-gameplay-spec.md`, plus une nouvelle lecture du wiki public de Stasis (index, FAQ, Premiers pas, Boucle de jeu, guides *Bien farmer*, *Monter son stuff*, *Préparer un donjon*). Leur contenu est résumé ici, pas recopié.

## Ce que la nouvelle lecture du wiki ajoute
- **Pas de patch notes ni de changelog publics.** Le wiki décrit seulement « les règles actuelles ». Les annonces passent par Discord, qui n'est pas lisible ici.
- Il y a des éléments d'interface que la spec ne mentionnait pas :
  - un **écran Zaap qui montre les bonus de chaque zone** (marée, archi, etc.) ;
  - un **suivi des archimonstres** ;
  - une **fenêtre Donjons** avec une fiche par donjon (achat de clé, GO) ;
  - une **confirmation qui affiche la destination et le prix** (reprise de salle ou nouvelle entrée payante) ;
  - un bouton **« Acheter pour le Duo »**.
- Règles ou précisions :
  - un passage de boss donne autant d'XP que le succès du donjon (les combats de boss à plusieurs vagues gardent leur XP) ;
  - avec Salle complète « Non », les salles s'adaptent à la taille du groupe ;
  - la PP n'influence jamais la jauge ;
  - le Raffineur est présenté comme le débouché normal pour tout ce qui n'est ni ressource ni candidat à la fusion.
- Le parcours du nouveau joueur est inchangé : la carte de départ n'est toujours pas décrite.

## Tableau des écarts (trié par impact sur un joueur solo)

Légende : ✅ Fait · 🟡 Partiel · ❌ Manquant · ⛔ Impossible sur ce client

| # | Fonction Stasis | État | Constat dans le code / ce qui manque |
|---|---|---|---|
| 1 | Passifs de classe (choix au niv. 50 parmi 3 de toutes les classes, mécaniques de combat, PvM seulement) | 🟡 | `dons.json` → `passives` : 2 passifs de **stats plates** par classe (ex. « +40 Puissance »), seulement ceux de sa classe. Il manque les 19 mécaniques (bouclier Féca, roulette Ecaflip…), le tirage parmi les 19 classes, les 2 relances gratuites et la désactivation en PvP. |
| 2 | Arbre de Recherche (21 recherches en 3 branches) | 🟡 | `research.json` n'a que 9 entrées. Plusieurs ont un autre sens ou un autre prix (voir la section Chiffres). Il manque : Élan, Pas léger, Dons duo, Métamorphose, Bi-élément, Résonance, Endurance, Ténacité, Action (+1 PA), Cadence, Ciblage, Évolution, ainsi que la vraie Polyvalence (configurations). |
| 3 | Barre de jauge permanente en haut de l'écran (coffre « ! », bouton comptoir, temps de marée) | 🟡 | La jauge existe côté serveur (`ZoneGauge.cs`) et dans l'onglet F9 « jauge ». Côté client (`ProgressionUi.Hud.cs`), on n'a qu'un bouton « Stasis » et des notifications. Pas de barre visible en continu. |
| 4 | HUD « Le Système » (mission de chapitre + 2 pistes, GO / Récupérer) | 🟡 | La logique est complète (`Missions.cs`, `.missions`, `.go`, `.recuperer`). Elle n'est visible que dans l'onglet F9 Missions, sans panneau permanent à l'écran. |
| 5 | Dons de sort (11 types d'amélioration, ~44 sorts par classe) | 🟡 | `spell_dons.json` : 129 dons au total, en 7 types (degats, soins, critique, portee, relance, pa, lancers). Il manque Insistance, Adaptabilité, Clairvoyance, Liberté, Persistance, Anticipation, les prérequis entre améliorations, et environ 90 % du catalogue. |
| 6 | Vitesse hors combat +25 % pour tous | ❌ | `Dons.SpeedPercent` ne compte que les dons. Il n'y a pas de base de +25 % (ni la recherche Pas léger). |
| 7 | Guides contextuels (affichés la 1re fois, relisibles dans Hub > Guides) | ❌ | Introuvable, ni côté serveur ni dans JondoFix. |
| 8 | Refontes (sorts, complète, passif) et « Garder ma run » | 🟡 | Seule la refonte de stats existe (`Dons.cs`, `.don refonte`). Il n'y a ni parchemin de refonte de sorts, ni refonte complète, ni oubli de passif. `Ascension.cs` ne propose pas « Garder ma run » (retrouver les offres). |
| 9 | Écart du barème d'Éclats d'Ascension | 🟡 | Formule `10 × (niv/50)²` : 50→10, 100→40, 200→160, 300→360. Le wiki donne 1 / 20 / 150 / 1 450. Au niveau 300, on donne 4× moins, alors que l'Évolution coûte 1 000. |
| 10 | Mécaniques « Sans » en donjon | ❌ (choix assumé) | Documenté comme non fait dans `DungeonRules.cs` : les mécaniques sont scriptées une par une. |
| 11 | Suivi des archimonstres (Encyclopédie / écran Zaap) | ❌ | Les archis existent (`Archimonsters`, `farm.json`) mais aucune liste visible : en jeu, zone, repop restant. |
| 12 | Écran Zaap avec les bonus de zone | ❌ | Rien ne relie le Zaap aux marées ni aux archis. |
| 13 | Fenêtre Donjons (fiche, achat de clé, GO, entrée gratuite restante) | 🟡 | Il y a `.donjons`, `.marchand cle` (achat de clé à distance) et `DungeonKeyring` (entrée gratuite du mardi). Pas de fenêtre fiche. Il manque aussi la reprise de la dernière salle après un wipe, et la confirmation qui affiche le prix. |
| 14 | Dofus stasifiés et Éveil | 🟡 | `Eveil.cs` et `.eveil` sont présents (parcours, défi). Leur fidélité n'a pas été vérifiée en détail. |
| 15 | Orbe reconstituante et FM S3/S4 (runes de transcendance seulement) | ❌ | Il y a la Rune Do Sort (`events.json`) mais pas d'orbe reconstituante. Aucune trace du refus des runes normales sur S3/S4. |
| 16 | Fabrication (résultat au **jet parfait S1**, S2 pour les recettes légendaires de la Fissure) | 🟡 | `stasis.json` → `craftedTier: 1` multiplie les jets **aléatoires** (choix volontaire). Le devis existe (`.devis`). |
| 17 | Fissure : fragments, légendaires, pénalité d'abandon | 🟡 | Vagues, clés, bonus de formation et Dofus garanti sont présents (`Fissure.cs`). Pas de fragments ni d'objets légendaires. |
| 18 | Drop direct S2 à 2 % (« Cristallisation profonde ») pour un perso non évolué | ❌ | `stasis.json` indique que les persos normaux droppent tout en S1. Seul `evolvedDropWeights` existe. |
| 19 | Bourse aux ogrines | ⛔/❌ | Pas d'ogrines (on a des jetons Event et des Jondo Coins). On pourrait faire une bourse Jetons↔Orbes, mais c'est peu utile en solo. |
| 20 | Duo (2 comptes dans une seule fenêtre) | 🟡 | Il y a un système de héros (`.heros`, `docs/heroes.md`). Les règles propres au Duo (clés partagées, « Acheter pour le Duo », interdiction en Time Attack et Poutch) n'apparaissent pas. |
| 21 | Bonus de recherche de donjon | ✅/🟡 | `DungeonRules` (+25/+50, préformé +15/+30) et `Matchmaking` sont en place. Pas de bonus d'Orbes pour un donjon complet (montant non publié). |
| 22 | Salle complète | ✅ | `DungeonRules.SetFullRoom`. Option gardée en mémoire seulement : perdue au redémarrage. |
| 23 | Jauge, ciblage, reset 72 h, seuils 1 / 1,5 / 2 | ✅ | `farm.json` → `Gauge`, `ZoneGauge.cs`. |
| 24 | Comptoir (Densité, Recrue, Étude, Jauge ; 30 min) | ✅ | `ZoneCounter.cs` et `farm.json` → `Counter`. |
| 25 | Marées (50 / 10 min, +250 % XP, tolérance 20, ×2) et calendrier | ✅ | `Tides.cs` et l'onglet « maree ». |
| 26 | Archimonstres (×5 Orbes, +400 % drop, jauge ×2, repop 1–3 h, zones d'entraînement) | ✅ | `farm.json` → `Archmonsters`. |
| 27 | Orbes (part complète, boss, malus de surniveau) | ✅ | `orbes.json` et `Orbes.cs` (Orbes = Jondo Coins). |
| 28 | Stasification S1–S4, fusion, Raffineur | ✅ | `stasis.json` et `.fusion`, `.raffiner`. |
| 29 | Paliers 200–300, accès anticipé aux zones, hardcore | ✅ | `Tiers.cs`, `tiers.json`. Cire Momore est remplacée par Martilloseco (absente du client). |
| 30 | Dons de niveau, d'aventure et Pension, rareté, pitié, touches 1–5 | ✅ | `dons.json`, `DonPopup.cs` (touches 1–5 et pavé numérique). |
| 31 | Pension (incubation, étude, accouplement, caisse) | ✅ | `pension.json` et `Pension.cs`. Les options payées en ogrines le sont en jetons. |
| 32 | Cartes de monstres et raffineur de cartes | ✅ | `MonsterCards.cs`. Cartes virtuelles, sans objet côté client. |
| 33 | Événements (Time Attack, Poutch, Traque) et boutique de jetons | ✅ | `StasisEvents.cs`. Des bots remplissent le classement. |
| 34 | Sanctuaire (aller-retour en 1 clic, Marchand Conseillé / Catalogue) | ✅ | `Sanctuary.cs`, bouton Sanctuaire du HUD, `.marchand acheter conseil`. |
| 35 | Récap de fin de combat (buffs, jauge) | ✅ | `FightRecaps.cs`. |
| 36 | Rang de prestige | ❌ | Effets non publiés par Stasis. Mentionné seulement. |
| 37 | F11 signalement de bug | ⛔/❌ | Chez nous, F11 ouvre déjà la console admin. Il faudrait une autre touche. |
| 38 | Compte Bubble multi-serveurs, launcher | ⛔ | Hors périmètre gameplay (on a notre propre launcher). |

## Top 10 : pistes d'implémentation

1. **Vrais passifs de classe.** Remplacer `passives.<breed>` dans `content/progression/dons.json` par 19 passifs qui ont un `effectKind` (déclencheurs de combat). Les gérer dans le moteur de combat avec un hook « début de combat / fin de tour / dégâts » : `Managers/ClassPassives.cs` existe déjà pour les bots, il peut servir de modèle. Dans `Dons.PassivesOf`, tirer 3 passifs parmi toutes les classes, avec 2 relances gratuites et 9 vus au maximum, et ne pas les appliquer en PvP.
2. **Compléter la Recherche.** Ajouter les entrées manquantes dans `content/progression/research.json`, avec de nouveaux `kind` dans `Managers/Stasis/Ascension.cs` : `speed`, `hpPercent`, `resPercent`, `donPercent`, `ap`, `castsPerTurn`, `castsPerTarget`, `catchUpLevels`, `duoCards`, `evolution`. Le rendu se fait dans l'onglet Recherche (`JondoFix/ProgressionUi.Panels.cs`). Aligner coûts et effets sur la spec §9.
3. **Barre de jauge HUD.** Dans `JondoFix/ProgressionUi.Hud.cs`, ajouter une barre fine en haut de l'écran : remplissage, « ! » quand il n'y a pas de cible, temps de marée restant, icônes des buffs du comptoir. Les données viennent d'une vue légère dans `Handlers/CommandHandler.PanelViews.cs` (le tab « jauge » existe déjà), interrogée à chaque fin de combat ou changement de carte.
4. **Panneau Système permanent.** Un petit bloc sous la barre de jauge : mission active, 2 pistes, boutons GO et Récupérer qui appellent `.go` / `.recuperer`. Se servir de la vue « missions » de `PanelViews.cs`. Fichiers : `ProgressionUi.Hud.cs`, `UiKit.cs`.
5. **Base de vitesse +25 %.** Dans `Managers/Stasis/Dons.cs` → `SpeedPercent`, ajouter une base configurable (`farm.json` ou `dons.json` : `baseWalkSpeedPercent: 25`) plus la recherche Pas léger. Le client l'applique déjà via `/api/stasis/speed`.
6. **Barème d'Éclats.** Dans `content/progression/ascension.json`, remplacer la formule par une table `eclatsByLevel` (50:1, 100:20, 150:64, 200:150, 225:400, 250:700, 275:1050, 300:1450) avec interpolation, lue par `Ascension.cs`.
7. **Guides contextuels.** Créer `content/progression/guides.json` (id, titre, texte rédigé par nous, système déclencheur). Côté serveur, enregistrer les guides vus dans `ProgressionStore.cs` et pousser une alerte à la première rencontre (jauge, comptoir, marée, fusion, Pension…). Côté client, une popup dans `ProgressionUi.Hud.cs` et un onglet « Guides » dans `ProgressionUi.Panels.cs`.
8. **Dons de sort enrichis.** Ajouter dans `content/progression/spell_dons.json` les kinds `insistance`, `ldv`, `ligne`, `poModifiable`, `cumul`, `delaiInitial`, plus un champ `requires`. Les appliquer dans `Managers/Stasis/SpellDons.cs` (modification des niveaux de sort envoyés et des contrôles de lancer côté combat). Étendre le catalogue vers une vingtaine de familles par classe.
9. **Refontes et « Garder ma run ».** Dans `Managers/Stasis/Dons.cs`, ajouter `ForgetSpells`, `FullRespec` et `RerollPassive`, payés en jetons Event comme la Pension. Dans `Ascension.cs`, enregistrer les offres du cycle et proposer `keep` (500 → prix en jetons). Commandes dans `Handlers/CommandHandler.Progression.cs`, boutons dans l'onglet DONS (`JondoFix/ProgressionUi.cs`).
10. **Suivi des archis et fenêtre Donjons.** Pour les archis, une vue « archis » dans `PanelViews.cs` (espèce, sous-zone, vivant ou temps de repop, depuis l'état d'`Archimonsters`) et un onglet F9. Pour les donjons, transformer la sortie de `.donjons` en fiche : entrée gratuite restante (`DungeonKeyring`), prix de la clé, GO, palier. Y ajouter la reprise de la dernière salle après wipe (`DungeonManager`), avec une confirmation qui montre destination et prix.

## Chiffres qui diffèrent du wiki / de la spec

| Sujet | Wiki (spec) | Chez nous |
|---|---|---|
| Éclats d'Ascension | 1 / 20 / 64 / 150 / 400 / 700 / 1 050 / 1 450 | `10×(niv/50)²` → 10 / 40 / 90 / 160 / … / 360 |
| Rattrapage | 3 rangs (20/40/60), ×2,25 / ×2,5 / ×3 ; plafond ×9 avec les autres multiplicateurs | 7 rangs (40→600), +1 par rang jusqu'à ×9 |
| Quatrième carte | 100 Éclats | 500 |
| Relance | 2 rangs (40/80), par offre | 3 rangs (60/120/240), par cycle |
| Seconde refonte | 50 | 250 |
| Tempo | vitesse d'animation +5/10/15 % | +150 Initiative par rang (5 rangs) |
| Polyvalence | 2 à 4 configurations dons+sorts+stuff (500 ×3) | +1 à la carte de caractéristique (200/450) |
| Moisson | n'existe pas | +10 % d'Éclats par rang (ajout maison) |
| Fabrication | jet parfait S1 | jets aléatoires × multiplicateur S1 |
| Drop S2 non évolué | 2 % | 0 % |
| Relance de don | coût croissant avec le niveau (non chiffré) | 1 000 kamas-équivalent × niveau |
| Prix ogrines (pity 350, places de Pension 300, accélération 20, Garder ma run 500) | ogrines | jetons Event (ex. place 30, accélération 2) ; pity et Garder ma run absents |
| Tier 300 « Caverne de Cire Momore » | présent | remplacé par Martilloseco (absent du client) |
| Fissure, Dofus garanti | 1 000 vagues éligibles | réglable dans `fissure.json` (`dofus.wavesNeeded`), non vérifié |

Chiffres conformes : jauge (72 h, 1 / 1,5 / 2, ×2 archi / marée / buff), marées (50 / 10, +250 %, tolérance 20), archis (×5, +400 %, 1–3 h), hardcore (PV ×3, dégâts ×2,25, drop ×2, crans 66/30/3/1), multiplicateurs S1–S4, bonus de recherche de donjon, portées et coûts d'accouplement des gén. 7–10, rareté des dons (20 / 5, Fortune 22-24-25 / 7-9-12), récompenses des événements.
