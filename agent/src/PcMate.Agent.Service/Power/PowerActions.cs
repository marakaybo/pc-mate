namespace PcMate.Agent.Service.Power;

public static class PowerActions
{
    public const string Shutdown = "shutdown";
    public const string Reboot = "reboot";
    public const string Sleep = "sleep";
    public const string Hibernate = "hibernate";
    public const string Lock = "lock";

    public static readonly HashSet<string> All =
        new(StringComparer.OrdinalIgnoreCase) { Shutdown, Reboot, Sleep, Hibernate, Lock };

    public static string RussianTitle(string action) => action switch
    {
        Shutdown => "Выключение компьютера",
        Reboot => "Перезагрузка компьютера",
        Sleep => "Переход в спящий режим",
        Hibernate => "Переход в гибернацию",
        Lock => "Блокировка компьютера",
        _ => action
    };

    public static string RussianVerb(string action) => action switch
    {
        Shutdown => "выключится",
        Reboot => "перезагрузится",
        Sleep => "уснёт",
        Hibernate => "уйдёт в гибернацию",
        Lock => "заблокируется",
        _ => action
    };
}
