namespace Ahoy.Simulation;

/// <summary>
/// Caribbean-appropriate name pool for procedurally generated NPCs (replacement governors, etc.).
/// </summary>
public static class NamePool
{
    private static readonly string[] FirstNames =
    [
        "Alejandro", "Diego", "Luis", "Carlos", "Miguel", "Rafael", "Fernando", "Rodrigo",
        "James", "William", "Thomas", "Henry", "Edward", "Richard", "Robert", "George",
        "Jean", "Pierre", "François", "Henri", "Louis", "Antoine", "Michel", "Charles",
        "Jan", "Pieter", "Willem", "Cornelis", "Adriaan", "Hendrik", "Jacob", "Dirk",
        "Maria", "Isabel", "Ana", "Catalina", "Elena", "Rosa", "Lucia", "Carmen",
        "Elizabeth", "Margaret", "Anne", "Catherine", "Jane", "Eleanor", "Agnes",
    ];

    private static readonly string[] LastNames =
    [
        "de Córdoba", "Herrera", "Guzmán", "Montoya", "Navarro", "Vega", "Reyes", "Soto",
        "Hawkins", "Drake", "Morgan", "Raleigh", "Frobisher", "Grenville", "Newport",
        "du Bois", "Moreau", "Lefebvre", "Girard", "Rousseau", "Leclerc", "Dupont",
        "van der Berg", "de Ruyter", "Heyn", "van Hoorn", "Tromp", "Sweers", "Piet",
        "Alcázar", "Castillo", "Fuentes", "Caballero", "Palomino", "Villanueva",
        "Blackwood", "Whitmore", "Ashton", "Pemberton", "Hartley", "Cromwell",
    ];

    public static string RandomFirst(Random rng) => FirstNames[rng.Next(FirstNames.Length)];
    public static string RandomLast(Random rng)  => LastNames[rng.Next(LastNames.Length)];
}
