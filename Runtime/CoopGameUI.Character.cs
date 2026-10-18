using System;
using FollowingSeasCoop.Net;
using FollowingSeasCoop.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FollowingSeasCoop.Runtime
{
    // The Character page of the co-op window: the player's own look. It takes the window's place
    // until Apply or Cancel, on the title screen and in the world, in a session or not. What is
    // chosen is a draft: only Apply writes the config and tells the others.
    internal static partial class CoopGameUI
    {
        private static GameObject _lookRow, _characterPage, _buttonsRow, _partsGrid;
        private static TextMeshProUGUI _lookText, _modelText, _partsNote;
        private static Button _lookChange, _modelPrev, _modelNext, _lookApply, _lookDefaults, _lookCancel;
        private static GridLayoutGroup _partsLayout;
        private static GameObject[] _partCells = new GameObject[0];
        private static TextMeshProUGUI[] _partLabels = new TextMeshProUGUI[0];
        private static Button[] _partButtons = new Button[0];
        private static bool _characterOpen;
        private static byte _draftModel;
        private static readonly byte[] _draftParts = new byte[Limits.AppearanceParts];

        private static void BuildLookRow()
        {
            _lookRow = Row(_window, "Character");
            _lookText = Text(_lookRow.transform, "Look", _plainFont, 18f, Color.white);
            _lookText.textWrappingMode = TextWrappingModes.NoWrap;
            _lookText.overflowMode = TextOverflowModes.Ellipsis;
            Size(_lookText.gameObject, -1f, -1f).flexibleWidth = 1f;
            _lookChange = MakeButton(_lookRow.transform, "Change", OpenCharacter, 110f);
        }

        private static void BuildCharacterPage()
        {
            RectTransform page = Node(_window, "Character");
            page.SetSiblingIndex(_heading.transform.GetSiblingIndex() + 1);
            var column = page.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 10f;
            column.childControlWidth = column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            _characterPage = page.gameObject;

            GameObject model = Row(page, "Model");
            _modelPrev = MakeButton(model.transform, "<", () => StepModel(-1), 40f);
            _modelText = Text(model.transform, "Model", _plainFont, 20f, Color.white);
            _modelText.alignment = TextAlignmentOptions.Center;
            Size(_modelText.gameObject, -1f, -1f).flexibleWidth = 1f;
            _modelNext = MakeButton(model.transform, ">", () => StepModel(1), 40f);

            _partsNote = Text(page, "Parts note", _bodyFont, 13f, Soft);

            RectTransform grid = Node(page, "Parts");
            _partsLayout = grid.gameObject.AddComponent<GridLayoutGroup>();
            _partsLayout.cellSize = new Vector2(205f, 34f);
            _partsLayout.spacing = new Vector2(10f, 6f);
            _partsLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            _partsLayout.constraintCount = 2;
            _partsGrid = grid.gameObject;
            int most = 0;
            for (byte m = 0; m < AppearanceCatalog.ModelCount; m++) most = Mathf.Max(most, AppearanceCatalog.PartCount(m));
            _partCells = new GameObject[most];
            _partLabels = new TextMeshProUGUI[most];
            _partButtons = new Button[most];
            for (int i = 0; i < most; i++)
            {
                int part = i;
                RectTransform cell = Node(grid, "Part " + i);
                var line = cell.gameObject.AddComponent<HorizontalLayoutGroup>();
                line.spacing = 8f;
                line.childAlignment = TextAnchor.MiddleLeft;
                line.childControlWidth = line.childControlHeight = true;
                line.childForceExpandWidth = line.childForceExpandHeight = false;
                _partLabels[i] = Text(cell, "Label", _bodyFont, 14f, Color.white);
                Size(_partLabels[i].gameObject, 80f, -1f);
                _partButtons[i] = MakeButton(cell, "", () => StepPart(part), -1f);
                _partCells[i] = cell.gameObject;
            }

            RectTransform buttons = Node(page, "Character buttons");
            var across = buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
            across.spacing = 12f;
            across.childControlWidth = across.childControlHeight = true;
            across.childForceExpandWidth = true;
            across.childForceExpandHeight = false;
            _lookApply = MakeButton(buttons, "Apply", ApplyCharacter, -1f);
            _lookDefaults = MakeButton(buttons, "Defaults", () => Array.Clear(_draftParts, 0, _draftParts.Length), -1f);
            _lookCancel = MakeButton(buttons, "Cancel", CloseCharacter, -1f);
            _characterPage.SetActive(false);
        }

        private static void OpenCharacter()
        {
            PlayerAppearance committed = CoopBehaviour.Instance.Appearance.Committed;
            _draftModel = committed.Model;
            Array.Clear(_draftParts, 0, _draftParts.Length);
            if (committed.Parts != null)
                Array.Copy(committed.Parts, _draftParts, Mathf.Min(committed.Parts.Length, _draftParts.Length));
            _characterOpen = true;
            _confirm = false;
            _note = "";
            Refresh(CoopBehaviour.Instance);
            Select(_modelNext);
        }

        private static void CloseCharacter()
        {
            if (!_characterOpen) return;
            _characterOpen = false;
            Refresh(CoopBehaviour.Instance);
            Select(_lookChange);
        }

        private static void StepModel(int by)
        {
            // Automatic first, then the models.
            int count = AppearanceCatalog.ModelCount + 1;
            int at = _draftModel == AppearanceCatalog.Unchosen ? 0 : _draftModel + 1;
            at = (at + by + count) % count;
            _draftModel = at == 0 ? AppearanceCatalog.Unchosen : (byte)(at - 1);
            // Another model's parts are other parts: its choices start from its own look.
            Array.Clear(_draftParts, 0, _draftParts.Length);
        }

        private static void StepPart(int part)
        {
            _draftParts[part] = (byte)((_draftParts[part] + 1) % AppearanceCatalog.ChoiceCount(_draftModel));
        }

        private static void ApplyCharacter()
        {
            CoopBehaviour coop = CoopBehaviour.Instance;
            PlayerAppearance draft = AppearanceCatalog.Normalize(new PlayerAppearance { Model = _draftModel, Parts = (byte[])_draftParts.Clone() });
            if (!draft.Same(coop.Appearance.Committed))
            {
                coop.Appearance.Commit(draft);
                coop.Net.SetLocalAppearance(coop.Appearance.Committed);
            }
            CloseCharacter();
        }

        private static string LookLine(PlayerAppearance look)
        {
            int changed = 0;
            if (look.Parts != null)
                foreach (byte part in look.Parts)
                    if (part != 0) changed++;
            return AppearanceCatalog.Name(look.Model) +
                   (changed == 0 ? "" : "   <alpha=#B0>" + changed + (changed == 1 ? " part" : " parts") + " changed<alpha=#FF>");
        }

        // Part of Refresh: true when the page has the window.
        private static bool RefreshCharacter(CoopBehaviour coop, bool linking)
        {
            _lookRow.SetActive(!linking && !_confirm && !_characterOpen);
            _lookText.text = LookLine(coop.Appearance.Committed);
            _characterPage.SetActive(_characterOpen);
            if (!_characterOpen)
            {
                _buttonsRow.SetActive(true);
                return false;
            }
            foreach (Transform child in _window)
                child.gameObject.SetActive(child.gameObject == _characterPage || child == _heading.transform);

            _heading.text = "Character";
            _modelText.text = AppearanceCatalog.Name(_draftModel);
            int count = AppearanceCatalog.PartCount(_draftModel);
            bool changed = false;
            _partsNote.text = _draftModel == AppearanceCatalog.Unchosen
                ? "You get a model by your place in the crew, so the crew's looks differ."
                : count == 0
                ? "This model has no parts to change."
                : "A part can wear what another model of the same build wears on it.";
            _partsGrid.SetActive(count > 0);
            // A grid's cells do not shrink with the window, and in the world it is as wide as the game's page.
            float inner = _window.rect.width - _window.GetComponent<VerticalLayoutGroup>().padding.horizontal;
            if (inner > 0f) _partsLayout.cellSize = new Vector2(Mathf.Floor((inner - _partsLayout.spacing.x) / 2f), 34f);
            for (int i = 0; i < _partCells.Length; i++)
            {
                bool used = i < count;
                _partCells[i].SetActive(used);
                if (!used) continue;
                changed |= _draftParts[i] != 0;
                _partLabels[i].text = AppearanceCatalog.PartLabel(_draftModel, i);
                Caption(_partButtons[i], AppearanceCatalog.Name(AppearanceCatalog.Donor(_draftModel, _draftParts[i])));
            }
            _lookDefaults.interactable = changed;

            _message.text = _note;
            _message.gameObject.SetActive(_note.Length > 0);
            _hint.text = "Everyone in the session sees this look. It is kept for your next games.";
            _hint.gameObject.SetActive(true);
            return true;
        }
    }
}
