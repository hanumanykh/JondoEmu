using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Il2CppCore.UILogic.Components;
using Il2CppCore.UILogic.Components.Figma;
using UnityEngine.UIElements;

namespace JondoFix
{
    /// <summary>
    /// The admin window's custom items tab: an item of the client made over -- the effects every new
    /// one is born with, and the look of another item when worn -- written to the server's
    /// content/items/custom.json through /api/customs/list, /api/customs/save and
    /// /api/customs/remove, and given at once through /api/personaje.
    /// </summary>
    internal static partial class AdminWorldUi
    {
        private static readonly Dictionary<string, Dictionary<string, string>> CustomTexts =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["custom.title"] = AdminItemsUi.L("Crear o cambiar un objeto custom", "Make or change a custom item", "Créer ou modifier un objet custom"),
                ["custom.hint"] = AdminItemsUi.L(
                    "Se hace sobre un objeto que existe: guarda su nombre, su icono y su nivel. Cada uno nuevo nace con estas características, y llevado tiene el aspecto del otro objeto. Los ids salen en la pestaña Objetos.",
                    "It is made over an existing item: it keeps its name, icon and level. Every new one is born with these characteristics, and worn it looks like the other item. The ids are in the Items tab.",
                    "Il se fait sur un objet existant : il garde son nom, son icône et son niveau. Chaque nouvel exemplaire naît avec ces caractéristiques et, porté, prend l'apparence de l'autre objet. Les ids sont dans l'onglet Objets."),
                ["custom.base"] = AdminItemsUi.L("Objeto base (id)", "Base item (id)", "Objet de base (id)"),
                ["custom.skin"] = AdminItemsUi.L("Aspecto de (id, opcional)", "Looks like (id, optional)", "Apparence de (id, facultatif)"),
                ["custom.stats"] = AdminItemsUi.L("Características (vacío = sin; negativo = malus)", "Characteristics (empty = none; negative = malus)", "Caractéristiques (vide = aucune ; négatif = malus)"),
                ["custom.save"] = AdminItemsUi.L("Guardar", "Save", "Enregistrer"),
                ["custom.give"] = AdminItemsUi.L("Guardar y dar", "Save and give", "Enregistrer et donner"),
                ["custom.clear"] = AdminItemsUi.L("Vaciar", "Clear", "Vider"),
                ["custom.list"] = AdminItemsUi.L("Objetos custom ({0})", "Custom items ({0})", "Objets custom ({0})"),
                ["custom.none"] = AdminItemsUi.L("Todavía no hay ninguno.", "There are none yet.", "Il n'y en a encore aucun."),
                ["custom.edit"] = AdminItemsUi.L("Cambiar", "Edit", "Modifier"),
                ["custom.remove"] = AdminItemsUi.L("Quitar", "Remove", "Retirer"),
                ["custom.looks"] = AdminItemsUi.L("aspecto de {0}", "looks like {0}", "apparence de {0}"),
                ["custom.saved"] = AdminItemsUi.L("Guardado: los {0} nuevos serán así.", "Saved: new {0} will be like this.", "Enregistré : les nouveaux {0} seront ainsi."),
                ["custom.given"] = AdminItemsUi.L("Guardado y dado.", "Saved and given.", "Enregistré et donné."),
                ["custom.removed"] = AdminItemsUi.L("Quitado: el objeto vuelve a ser el de siempre.", "Removed: the item is its usual self again.", "Retiré : l'objet redevient normal."),
                ["custom.bad_base"] = AdminItemsUi.L("Escribe el id del objeto base.", "Type the base item's id.", "Tapez l'id de l'objet de base."),
                ["error.objeto-desconocido"] = AdminItemsUi.L("El servidor no conoce ese objeto.", "The server does not know that item.", "Le serveur ne connaît pas cet objet."),
                ["error.sin-skin"] = AdminItemsUi.L("No se conoce el aspecto de ese objeto: elige otro.", "That item's look is not known: pick another.", "L'apparence de cet objet n'est pas connue : choisissez-en un autre."),
                ["error.custom-vacio"] = AdminItemsUi.L("Pon un aspecto o alguna característica.", "Give it a look or some characteristic.", "Donnez-lui une apparence ou une caractéristique."),
            };

        /// <summary>The characteristics offered, by the name the server's file uses, and their label.</summary>
        private static readonly (string Key, string Es, string En, string Fr)[] CustomStats =
        {
            ("vitalite", "Vitalidad", "Vitality", "Vitalité"), ("sagesse", "Sabiduría", "Wisdom", "Sagesse"),
            ("force", "Fuerza", "Strength", "Force"), ("intelligence", "Inteligencia", "Intelligence", "Intelligence"),
            ("chance", "Suerte", "Chance", "Chance"), ("agilite", "Agilidad", "Agility", "Agilité"),
            ("puissance", "Potencia", "Power", "Puissance"), ("pa", "PA", "AP", "PA"), ("pm", "PM", "MP", "PM"),
            ("po", "Alcance", "Range", "PO"), ("dommages", "Daños", "Damage", "Dommages"),
            ("soins", "Curas", "Heals", "Soins"), ("critique", "Críticos", "Critical", "Critique"),
            ("initiative", "Iniciativa", "Initiative", "Initiative"), ("prospection", "Prospección", "Prospecting", "Prospection"),
            ("invocations", "Invocaciones", "Summons", "Invocations"), ("tacle", "Placaje", "Lock", "Tacle"),
            ("fuite", "Huida", "Dodge", "Fuite"), ("pods", "Pods", "Pods", "Pods"),
        };

        private static TextInput _customBase, _customSkin;
        private static readonly Dictionary<string, TextInput> _customFields = new Dictionary<string, TextInput>();
        private static VisualElement _customList;
        private static DofusLabel _customListTitle;

        private static string CT(string key, params object[] values)
        {
            if (!CustomTexts.TryGetValue(key, out var byLanguage)) return T(key, values);
            string text = byLanguage.TryGetValue(KoliseoUi.Language ?? "en", out string own) ? own : byLanguage["en"];
            return values.Length == 0 ? text : string.Format(CultureInfo.InvariantCulture, text, values);
        }

        private static string StatLabel((string Key, string Es, string En, string Fr) stat)
            => (KoliseoUi.Language ?? "en") switch { "es" => stat.Es, "fr" => stat.Fr, _ => stat.En };

        private static void ResetCustom()
        {
            _customBase = _customSkin = null;
            _customFields.Clear();
            _customList = null;
            _customListTitle = null;
        }

        private static VisualElement BuildCustom()
        {
            var page = Page(scroll: true);
            page.Add(Section(CT("custom.title")));
            page.Add(Hint(CT("custom.hint")));

            var ids = Flow();
            _customBase = Field(ids, CT("custom.base"), true, 200f, "2474");
            _customSkin = Field(ids, CT("custom.skin"), true, 220f, "7921");
            page.Add(ids);

            page.Add(AdminItemsUi.Label(CT("custom.stats"), true));
            var stats = Flow();
            stats.style.marginTop = new StyleLength(6f);
            foreach (var stat in CustomStats)
                _customFields[stat.Key] = Field(stats, StatLabel(stat), false, 130f);
            page.Add(stats);

            page.Add(Players(true));
            var actions = Flow();
            actions.style.marginTop = new StyleLength(8f);
            actions.Add(ClientButton(CT("custom.save"), false, () => SaveCustom(give: false)));
            actions.Add(ClientButton(CT("custom.give"), true, () => SaveCustom(give: true)));
            actions.Add(ClientButton(CT("custom.clear"), false, () => FillCustom(0, 0, new Dictionary<string, long>())));
            page.Add(actions);
            page.Add(Status());

            var listSection = Section(CT("custom.list", 0));
            _customListTitle = listSection.Q<DofusLabel>();
            page.Add(listSection);
            _customList = new VisualElement();
            page.Add(_customList);
            Ask("customs/list", new Dictionary<string, object>(), ShowCustoms, quiet: true);
            return page;
        }

        private static void FillCustom(int gid, int skinOf, Dictionary<string, long> effects)
        {
            _customBase?.SetValueWithoutNotify(gid > 0 ? gid.ToString(CultureInfo.InvariantCulture) : "");
            _customSkin?.SetValueWithoutNotify(skinOf > 0 ? skinOf.ToString(CultureInfo.InvariantCulture) : "");
            foreach (var (key, field) in _customFields)
                field?.SetValueWithoutNotify(effects.TryGetValue(key, out long v) ? v.ToString(CultureInfo.InvariantCulture) : "");
        }

        private static void SaveCustom(bool give)
        {
            if (!int.TryParse(Read(_customBase), out int gid) || gid <= 0)
            {
                Say(CT("custom.bad_base"));
                return;
            }
            int.TryParse(Read(_customSkin), out int skinOf);
            var effects = new Dictionary<string, object>();
            foreach (var (key, field) in _customFields)
                if (long.TryParse(Read(field), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v) && v != 0)
                    effects[key] = v;

            var body = new Dictionary<string, object> { ["base"] = gid, ["effects"] = effects };
            if (skinOf > 0) body["skinOf"] = skinOf;
            string who = _who;
            Ask("customs/save", body, root =>
            {
                ShowCustoms(root);
                if (!give)
                {
                    Say(CT("custom.saved", gid));
                    return;
                }
                Ask("personaje", new Dictionary<string, object>
                {
                    ["personaje"] = who, ["objeto"] = gid, ["cantidad"] = 1, ["modo"] = "max",
                }, _ => Say(CT("custom.given")));
            });
        }

        private static void ShowCustoms(JsonElement root)
        {
            if (_customList == null || !root.TryGetProperty("items", out var items)) return;
            _customList.Clear();
            int count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                int gid = item.GetProperty("base").GetInt32();
                int skinOf = item.TryGetProperty("skinOf", out var s) ? s.GetInt32() : 0;
                string name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var effects = new Dictionary<string, long>();
                if (item.TryGetProperty("effects", out var e))
                    foreach (var stat in e.EnumerateObject()) effects[stat.Name] = stat.Value.GetInt64();

                string stats = string.Join(", ", effects.Select(x =>
                {
                    var known = CustomStats.FirstOrDefault(c => c.Key == x.Key);
                    string label = known.Key != null ? StatLabel(known) : x.Key;
                    return (x.Value > 0 ? "+" : "") + x.Value.ToString(CultureInfo.InvariantCulture) + " " + label;
                }));
                string text = $"{gid} · {name}" + (skinOf > 0 ? " · " + CT("custom.looks", skinOf) : "")
                              + (stats.Length > 0 ? " · " + stats : "");

                var line = AdminItemsUi.Row();
                line.style.alignItems = new StyleEnum<Align>(Align.Center);
                line.style.marginBottom = new StyleLength(6f);
                line.style.backgroundColor = new StyleColor(AdminItemsUi.RowPlain);
                line.style.paddingLeft = new StyleLength(8f);
                var label = AdminItemsUi.Label(text);
                label.style.flexGrow = new StyleFloat(1f);
                label.style.flexShrink = new StyleFloat(1f);
                label.style.whiteSpace = new StyleEnum<WhiteSpace>(WhiteSpace.Normal);
                line.Add(label);
                line.Add(ClientButton(CT("custom.edit"), false, () => FillCustom(gid, skinOf, effects)));
                line.Add(ClientButton(CT("custom.remove"), false, () =>
                    Ask("customs/remove", new Dictionary<string, object> { ["base"] = gid }, r =>
                    {
                        ShowCustoms(r);
                        Say(CT("custom.removed"));
                    })));
                _customList.Add(line);
            }
            if (count == 0) _customList.Add(Hint(CT("custom.none")));
            if (_customListTitle != null) _customListTitle.text = CT("custom.list", count);
        }

        private static string RefusalOfCustom(string reason)
            => CustomTexts.ContainsKey("error." + reason) ? CT("error." + reason) : null;
    }
}
