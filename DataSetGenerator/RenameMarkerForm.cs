using System;
using System.Windows.Forms;


namespace DataSetGenerator
{
    public sealed class RenameMarkerForm : Form
    {
        private readonly TextBox textBox;

        public string MarkerName => textBox.Text.Trim();
        public bool DeleteRequested { get; private set; }

        public RenameMarkerForm(string currentName)
        {
            Text = "Marker";
            Width = 300;
            Height = 140;
            StartPosition = FormStartPosition.CenterParent;

            textBox = new TextBox
            {
                Dock = DockStyle.Top,
                Text = currentName
            };

            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 40,
                FlowDirection = FlowDirection.RightToLeft
            };

            var okButton = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK
            };

            var cancelButton = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel
            };

            var deleteButton = new Button
            {
                Text = "Delete"
            };

            deleteButton.Click += (_, __) =>
            {
                DeleteRequested = true;
                DialogResult = DialogResult.OK;
                Close();
            };

            buttonPanel.Controls.Add(okButton);
            buttonPanel.Controls.Add(cancelButton);
            buttonPanel.Controls.Add(deleteButton);

            Controls.Add(textBox);
            Controls.Add(buttonPanel);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }
    }
}
