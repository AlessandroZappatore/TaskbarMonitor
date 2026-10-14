using System;
using System.Drawing;
using System.Windows.Forms;

// Finestra per scegliere cosa mostrare in ognuna delle 6 posizioni. Le modifiche si applicano subito.
class SettingsForm : Form
{
    class Item
    {
        public Metric M;
        public string Text;
        public bool Header; // intestazione di categoria: non selezionabile
        public override string ToString() { return Text; }
    }

    ComboBox[] combos = new ComboBox[Metrics.SlotCount];
    int[] lastIndex = new int[Metrics.SlotCount];
    Metric[] slots;
    Action<Metric[]> apply;
    bool busy;

    public SettingsForm(Metric[] current, Action<Metric[]> apply)
    {
        this.apply = apply;
        slots = (Metric[])current.Clone();

        Text = "Taskbar Monitor - Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Font;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 4, RowCount = 5, Dock = DockStyle.Fill };

        var intro = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Text = "Choose what each position shows (up to 6 values). Positions are laid out in 3 columns of 2. " +
                   "Leave a position empty to remove it; a column with two empty positions disappears and the widget gets narrower. " +
                   "Changes apply immediately.",
            Margin = new Padding(0, 0, 0, 12)
        };
        table.Controls.Add(intro, 0, 0);
        table.SetColumnSpan(intro, 4);

        for (int c = 0; c < 3; c++)
            table.Controls.Add(new Label { Text = "Column " + (c + 1), AutoSize = true, Margin = new Padding(0, 0, 0, 4) }, c + 1, 1);

        string[] rows = { "Top", "Bottom" };
        for (int r = 0; r < 2; r++)
        {
            table.Controls.Add(new Label { Text = rows[r], AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 10, 0) }, 0, r + 2);
            for (int c = 0; c < 3; c++)
            {
                int slot = c * 2 + r; // colonna c, riga r
                var cb = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    DrawMode = DrawMode.OwnerDrawFixed,
                    Width = 190,
                    MaxDropDownItems = 30,
                    Margin = new Padding(0, 3, 12, 3)
                };
                cb.Items.Add(new Item { M = Metric.None, Text = Metrics.Name(Metric.None) });
                foreach (var g in Metrics.Groups)
                {
                    cb.Items.Add(new Item { Header = true, Text = g.Title });
                    foreach (var m in g.Items) cb.Items.Add(new Item { M = m, Text = Metrics.Name(m) });
                }
                combos[slot] = cb;
                int s = slot;
                cb.DrawItem += DrawComboItem;
                cb.SelectedIndexChanged += (o, e) => OnSelect(s);
                table.Controls.Add(cb, c + 1, r + 2);
            }
        }

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 14, 0, 0) };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (o, e) => Close(); // finestra non modale: DialogResult non la chiude
        var reset = new Button { Text = "Reset to default", AutoSize = true };
        reset.Click += (o, e) => { slots = Metrics.Default(); ShowSlots(); apply((Metric[])slots.Clone()); };
        buttons.Controls.Add(close);
        buttons.Controls.Add(reset);
        table.Controls.Add(buttons, 0, 4);
        table.SetColumnSpan(buttons, 4);
        AcceptButton = close;
        CancelButton = close;

        Controls.Add(table);
        ShowSlots();
    }

    // Voci con rientro sotto la loro categoria; le intestazioni sono in grassetto e non si possono scegliere.
    void DrawComboItem(object sender, DrawItemEventArgs e)
    {
        var cb = (ComboBox)sender;
        if (e.Index < 0) return;
        var it = (Item)cb.Items[e.Index];
        bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
        bool selected = !it.Header && (e.State & DrawItemState.Selected) != 0 && inList;
        Color back = selected ? SystemColors.Highlight : (it.Header && inList ? SystemColors.Control : SystemColors.Window);
        Color fore = selected ? SystemColors.HighlightText : (it.Header ? SystemColors.GrayText : SystemColors.WindowText);
        using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
        int indent = (inList && !it.Header && it.M != Metric.None) ? 14 : 0;
        using (var f = it.Header ? new Font(cb.Font, FontStyle.Bold) : null)
        using (var fb = new SolidBrush(fore))
        {
            var rect = new Rectangle(e.Bounds.X + 4 + indent, e.Bounds.Y, e.Bounds.Width - 4 - indent, e.Bounds.Height);
            var sf = new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter };
            e.Graphics.DrawString(it.Text, f ?? cb.Font, fb, rect, sf);
            sf.Dispose();
        }
    }

    // Se si finisce su un'intestazione (clic o frecce da tastiera) si salta alla voce successiva nella stessa direzione.
    void OnSelect(int i)
    {
        if (busy) return;
        var cb = combos[i];
        var it = cb.SelectedItem as Item;
        if (it != null && it.Header)
        {
            // freccia su da tastiera (un passo indietro): continua verso l'alto; clic o altri salti: prima voce sotto l'intestazione
            int dir = (cb.SelectedIndex - lastIndex[i] == -1) ? -1 : 1;
            int j = cb.SelectedIndex + dir;
            while (j >= 0 && j < cb.Items.Count && ((Item)cb.Items[j]).Header) j += dir;
            busy = true;
            cb.SelectedIndex = (j >= 0 && j < cb.Items.Count) ? j : lastIndex[i];
            busy = false;
            if (cb.SelectedIndex == lastIndex[i]) return;
        }
        lastIndex[i] = cb.SelectedIndex;
        Changed(i);
    }

    void ShowSlots()
    {
        busy = true;
        for (int i = 0; i < combos.Length; i++)
            for (int j = 0; j < combos[i].Items.Count; j++)
            {
                var it = (Item)combos[i].Items[j];
                if (!it.Header && it.M == slots[i]) { combos[i].SelectedIndex = j; lastIndex[i] = j; break; }
            }
        busy = false;
    }

    void Changed(int i)
    {
        if (busy) return;
        Metric nm = ((Item)combos[i].SelectedItem).M;
        // un dato non puo' comparire due volte: se e' gia' altrove, le due posizioni si scambiano
        if (nm != Metric.None)
            for (int j = 0; j < slots.Length; j++)
                if (j != i && slots[j] == nm) slots[j] = slots[i];
        slots[i] = nm;
        ShowSlots();
        apply((Metric[])slots.Clone());
    }
}
