namespace BudzetDomowy.Core.Domain;

/// <summary>
/// Starting set of categories. Ids are derived from the names, so two devices seeding at the same time
/// produce the same entities instead of duplicates.
/// </summary>
public static class DefaultCategories
{
    public static IReadOnlyList<Category> Create()
    {
        var list = new List<Category>();
        var order = 0;

        void Add(string name, CategoryType type, string color, string icon, params string[] children)
        {
            var parent = Make(name, null, type, color, icon, order++);
            list.Add(parent);
            var childOrder = 0;
            foreach (var child in children)
                list.Add(Make(child, parent, type, color, "", childOrder++));
        }

        Add("Spożywcze", CategoryType.Expense, "#43A047", "🛒", "Supermarket", "Piekarnia", "Mięso i wędliny", "Owoce i warzywa");
        Add("Cukiernicze", CategoryType.Expense, "#EC407A", "🍰");
        Add("Chemia", CategoryType.Expense, "#26C6DA", "🧴");
        Add("Dla domu", CategoryType.Expense, "#8D6E63", "🏠", "Wyposażenie", "Remonty i naprawy");
        Add("Rachunki", CategoryType.Expense, "#FB8C00", "🧾", "Czynsz", "Prąd", "Gaz", "Internet i telefon", "Subskrypcje");
        Add("Auto", CategoryType.Expense, "#546E7A", "🚗", "Paliwo", "Serwis i naprawy", "Ubezpieczenie", "Parking i opłaty");
        Add("Transport", CategoryType.Expense, "#5C6BC0", "🚌", "Komunikacja miejska", "Taksówki", "Pociągi i autobusy");
        Add("Zdrowie", CategoryType.Expense, "#E53935", "💊", "Lekarze", "Apteka", "Okulista i dentysta");
        Add("Dzieci", CategoryType.Expense, "#FFB300", "🧸", "Szkoła i przedszkole", "Kieszonkowe", "Zabawki");
        Add("Ubrania/Buty", CategoryType.Expense, "#8E24AA", "👕");
        Add("Alkohol", CategoryType.Expense, "#6D4C41", "🍷");
        Add("Rozrywka", CategoryType.Expense, "#00ACC1", "🎬", "Restauracje i kawiarnie", "Kino i kultura", "Sport i basen", "Hobby");
        Add("Luksusowe", CategoryType.Expense, "#D4AF37", "💎");
        Add("Edukacja", CategoryType.Expense, "#3949AB", "📚");
        Add("Prezenty i darowizny", CategoryType.Expense, "#F06292", "🎁");
        Add("Inne", CategoryType.Expense, "#78909C", "📦");

        Add("Wynagrodzenie", CategoryType.Income, "#2E7D32", "💼");
        Add("Świadczenia", CategoryType.Income, "#388E3C", "🏛");
        Add("Inne przychody", CategoryType.Income, "#66BB6A", "💰");

        return list;
    }

    public static Guid IdFor(string name, string? parentName = null) =>
        DeterministicId.FromString("seed-category:" + (parentName is null ? name : parentName + "/" + name));

    private static Category Make(string name, Category? parent, CategoryType type, string color, string icon, int order) => new()
    {
        Id = IdFor(name, parent?.Name),
        Name = name,
        ParentId = parent?.Id,
        Type = type,
        Color = color,
        Icon = icon,
        SortOrder = order,
    };
}
