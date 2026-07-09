namespace TimeTracker;

/// <summary>
/// Простая форма "как вас зовут", показывается один раз при первом запуске
/// (и повторно через пункт трей-меню "Сменить сотрудника"). Собрана в коде,
/// без Designer-файла — форма из трёх контролов не оправдывает отдельный partial.
/// </summary>
public class FirstRunForm : Form
{
    private readonly TextBox _nameBox;

    public string EmployeeName => _nameBox.Text.Trim();

    public FirstRunForm(string initialName = "")
    {
        Text = "Учёт времени — сотрудник";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(360, 130);

        var label = new Label
        {
            Text = "Введите ваше имя и фамилию:",
            AutoSize = true,
            Location = new Point(16, 16)
        };

        _nameBox = new TextBox
        {
            Text = initialName,
            Location = new Point(16, 42),
            Width = 328
        };

        var okButton = new Button
        {
            Text = "Сохранить",
            DialogResult = DialogResult.OK,
            Location = new Point(188, 80),
            Width = 80
        };
        okButton.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_nameBox.Text))
            {
                MessageBox.Show(this, "Имя не может быть пустым.", "Учёт времени",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };

        var cancelButton = new Button
        {
            Text = "Отмена",
            DialogResult = DialogResult.Cancel,
            Location = new Point(272, 80),
            Width = 72
        };

        Controls.Add(label);
        Controls.Add(_nameBox);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;
    }
}
