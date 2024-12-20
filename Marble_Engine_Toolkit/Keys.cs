using System.Text.Json;

namespace Marble
{
    public class GameKeysManager
    {
        private const string JsonFilePath = "gamekeys.json";
        private Dictionary<string, string> gameKeys;

        public GameKeysManager()
        {
            LoadOrInitializeGameKeys();
        }

        private void LoadOrInitializeGameKeys()
        {
            if (File.Exists(JsonFilePath))
            {
                string jsonContent = File.ReadAllText(JsonFilePath);
                gameKeys = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonContent);
            }
            else
            {
                gameKeys = new Dictionary<string, string>
                {
                    { "Amai Seikatsu", "amai_seikatu" },
                    { "Elf no Futago Hime Willan to Arsura", "ALSRA" },
                    { "-Mofukuzuma-", "喪服妻-" },
                    { "-Furinkanzan-", "HIGONOKAMI" },
                    { "Himemiko", "白兎隊は超サイコー" },
                    { "Hitozuma Sakunyuu Hanten", "人妻搾乳飯店-0630-2006" },
                    { "Inmu Gakuen", "MAYURI" },
                    { "Kunoichi Sakuya", "くのいち・咲夜-0331-2006" },
                    { "Onna Kyoushi Yuuko", "女教師ゆうこ1968" },
                    { "Onsoku Hishou Sonic Mercedes", "MELCEDES" },
                    { "Oshaburi Announcer", "osyaburiana" },
                    { "Rinkan Byoutou", "-輪奸病棟-\u3000ルネTeamBitters PRESENTS" },
                    { "Shifoku Mermaid", "しおふきマーメイド-0330-2007" },
                    { "Shoujo Senki Soul Eater", "BLOODSUCKER" },
                    { "Tsurutsuru Nurse", "t2_nurse" }
                };
                SaveGameKeys();
            }
        }

        public void SaveGameKeys()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            string jsonString = JsonSerializer.Serialize(gameKeys, options);
            File.WriteAllText(JsonFilePath, jsonString);
        }

        public void AddGameKey(string gameName, string key)
        {
            if (!gameKeys.ContainsKey(gameName))
            {
                gameKeys[gameName] = key;
                SaveGameKeys();
                Console.WriteLine($"Added new game: {gameName} with key: {key}");
            }
            else
            {
                Console.WriteLine($"Game {gameName} already exists with key: {gameKeys[gameName]}");
            }
        }

        public string PromptForKey()
        {
            Console.WriteLine("\nAvailable games:");
            var gamesList = gameKeys.ToList();
            
            for (int i = 0; i < gamesList.Count; i++)
            {
                Console.WriteLine($"{i + 1}. {gamesList[i].Key}");
            }

            while (true)
            {
                Console.WriteLine("\nEnter the number of the game to select (or 'A' to add a new game):");
                string input = Console.ReadLine();

                if (input?.ToUpper() == "A")
                {
                    AddNewGameInteractive();
                    continue;
                }

                if (int.TryParse(input, out int selectedIndex) && 
                    selectedIndex > 0 && 
                    selectedIndex <= gamesList.Count)
                {
                    var selected = gamesList[selectedIndex - 1];
                    Console.WriteLine($"Selected game: {selected.Key}");
                    Console.WriteLine($"Key: {selected.Value}");
                    return selected.Value;
                }

                Console.WriteLine("Invalid selection. Please try again.");
            }
        }

        private void AddNewGameInteractive()
        {
            Console.WriteLine("\nEnter the game name:");
            string gameName = Console.ReadLine();

            Console.WriteLine("Enter the game key:");
            string gameKey = Console.ReadLine();

            if (!string.IsNullOrWhiteSpace(gameName) && !string.IsNullOrWhiteSpace(gameKey))
            {
                AddGameKey(gameName, gameKey);
            }
            else
            {
                Console.WriteLine("Invalid input. Game not added.");
            }
        }
    }
}