using System.Windows;
using BudzetDomowy.App.Infrastructure;

namespace BudzetDomowy.App.Views;

public partial class PaymentReminderWindow : Window
{
    public ReminderAnswer Answer { get; private set; } = ReminderAnswer.Later;

    public PaymentReminderWindow(PaymentReminder reminder)
    {
        DataContext = reminder;
        InitializeComponent();
        UiScaling.Attach(this);
    }

    private void Later_Click(object sender, RoutedEventArgs e) { Answer = ReminderAnswer.Later; DialogResult = false; }

    private void PayNow_Click(object sender, RoutedEventArgs e) { Answer = ReminderAnswer.PayNow; DialogResult = true; }

    private void Paid_Click(object sender, RoutedEventArgs e) { Answer = ReminderAnswer.Paid; DialogResult = true; }
}
