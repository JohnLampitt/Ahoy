# World Content Design: The Caribbean

**Document version:** 0.1
**Status:** Draft
**Feeds into:** `SDD-WorldState.md` (data structures), `Ahoy.WorldData` project (CaribbeanWorldDefinition)

---

## 1. Overview

This document defines the hand-crafted world content for the Caribbean theatre. It is the authoritative source for:

- The five simulation regions and their adjacency graph
- All named ports, their economic archetypes, controlling factions, and base trade profiles
- The six factions — four colonial powers and two pirate brotherhoods — with initial standing, goals, and personality
- Starting relationships between factions (the diplomatic web at world-start)
- The starting world date and initial economic conditions
- The governor and notable NPC pool at world-start

All values here translate directly into the `CaribbeanWorldDefinition` class in `Ahoy.WorldData`. They are not procedurally generated — this is the fixed foundation on which emergent simulation runs.

---

## 2. Starting Date

**1 January 1680**

This places the world at the height of the Buccaneering Era — Morgan's raid on Panama is recent history, the Treaty of Windsor has theoretically ended English privateering against Spain but is widely ignored, and the Brethren of the Coast are at their organisational peak. The great colonial crackdowns of the 1690s and 1700s have not yet arrived.

Implications:
- Pirate factions are strong at start; their natural arc is gradual suppression
- Spain is powerful but overextended; England is growing but not yet dominant
- The Dutch are commercially dominant but militarily weakened after the Anglo-Dutch Wars
- France is ascending under Louis XIV; aggressive but focused on Europe

---

## 3. The Five Regions

### Region Adjacency Graph

```
  FLORIDA CHANNEL ──── HISPANIOLA WATERS ──── LEEWARD CHAIN
                              │                      │
                         SPANISH MAIN ─────── WINDWARD REACH
```

Travel between non-adjacent regions is not possible in a single move. All sea routes pass through adjacent regions. This graph creates strategic chokepoints: Hispaniola Waters is the central hub; the Spanish Main and Windward Reach form the southern arc.

---

### 3.1 Florida Channel

**Geography:** The northern arc — the Bahama banks, the Florida straits, Cuba's north coast, and the scattered cays and shoals between them. Shallow waters and shifting sandbars make it treacherous for deep-draught warships; ideal for small fast vessels.

**Strategic character:** Gateway between the Atlantic and the Caribbean proper. Most Spanish treasure galleons pass through here on the homeward leg. English Nassau is the informal northern waypoint for traders and opportunists alike.

**LOD tier at start:** Distant (far from any initial player position — will be Regional or Local depending on player starting region choice)

**Seasonal hazard:** High hurricane exposure June–November; storms generated here often track westward.

---

### 3.2 Hispaniola Waters

**Geography:** The central Caribbean — the island of Hispaniola (divided between French Saint-Domingue and Spanish Santo Domingo), Jamaica to the southwest, and the waters between. The Windward Passage connects to the Atlantic; the Cayman Trench lies to the west.

**Strategic character:** The political and commercial heart of the Caribbean. Port Royal is the greatest trade entrepôt in the New World. Tortuga is the pirate capital. Spanish Santo Domingo is the oldest European city in the Americas. This is where every faction has interests and almost every major event reverberates.

**LOD tier at start:** Recommended as the starting region for the player. Local.

---

### 3.3 Leeward Chain

**Geography:** The northern Lesser Antilles — the volcanic arc from the Virgin Islands south to Dominica. Sint Eustatius (Statia) sits in the middle like a jewel: a tiny Dutch island that handles more trade tonnage than most colonial capitals.

**Strategic character:** The Dutch neutral-trading heart of the Caribbean. English and French plantations line the arc; Dutch merchants service all of them without scruple. Knowledge flows freely here — Statia's traders know everything and sell most of it.

**LOD tier at start:** Regional (one hop from Hispaniola Waters).

---

### 3.4 Windward Reach

**Geography:** The southern Lesser Antilles — Martinique, St. Lucia, St. Vincent, Grenada, Barbados (far to the east), and Trinidad at the southern end. More exposed to Atlantic weather; Barbados catches the trade winds first.

**Strategic character:** England's plantation powerhouse. Barbados produces half the Caribbean's sugar by value. France holds Martinique with a major naval station. The second pirate brotherhood — the Black Company — haunts the southern cays, preying on ships rounding the arc.

**LOD tier at start:** Distant (two hops from Hispaniola Waters via Leeward Chain).

---

### 3.5 Spanish Main

**Geography:** The South American and Central American coastline — Cartagena de Indias, the Gulf of Venezuela (Maracaibo), Portobelo (Panama terminus), and the Venezuelan coast. The Darien jungle and the Isthmus sit at the western end.

**Strategic character:** The treasury of the New World. Silver from Peru and emeralds from Colombia flow through here to Spain. Cartagena is one of the most fortified cities in the Americas. Spain's grip here is tightest; other factions operate only through Dutch Curaçao, which serves as the unofficial neutral port of the entire southern coast.

**LOD tier at start:** Distant (two hops from Hispaniola Waters via either Leeward Chain or direct).

---

## 4. Ports

### Economic Archetypes

| Archetype | Produces | Demands | Notes |
|---|---|---|---|
| **Colonial Capital** | Administrative goods, ships | Luxury goods, weapons | High authority, strong garrison |
| **Plantation Colony** | Sugar, Rum, Tobacco, Cotton | Labour (slaves), Tools, Food | Prosperity tied to crop prices |
| **Trade Hub** | None (intermediary) | Everything | High merchant density, knowledge brokers present |
| **Naval Base** | None | Provisions, Timber, Rope | Military strength high; civilian trade restricted |
| **Pirate Haven** | None | Ship repairs, Provisions | No official law; black market; information freely sold |
| **Treasure Port** | Precious metals, Gems | Provisions, Ships | Heavily fortified; high-value cargo; Spanish dominance |
| **Fishing Port** | Fish, Salt | Tools, Cloth | Small, low military; knowledge of local waters |
| **Smuggling Post** | Contraband (mixed) | Anything taxed elsewhere | Low official presence; neutral or absent faction control |

---

### 4.1 Florida Channel Ports

#### Havana *(Spain — Colonial Capital)*
- **Port ID key:** `havana`
- **Location:** NW Cuba, commanding the Gulf of Mexico exit
- **Prosperity:** 72 / 100
- **Governor Authority:** 80 / 100
- **Controlling faction:** Spain
- **Garrison:** Large (3 naval vessels, 400 soldiers)
- **Facilities:** Shipyard (full), Chandler, Broker, Church, Prison
- **Economic profile:**
  - Produces: Ship components, Cured provisions, Cigars
  - Demands: Luxury cloth, Spices, Weapons
  - Base price modifiers: Ship components –30% (oversupply), Luxury goods +40%
- **Notes:** Muster point for the Flota de Indias homeward voyage. The governor is a senior Armada officer, not a civilian. Spain's naval command for the Florida Channel operates from here. Knowledge of treasure fleet schedules is a premium commodity.

#### Nassau *(England — Trade Hub / Pirate-Adjacent)*
- **Port ID key:** `nassau`
- **Location:** New Providence Island, Bahamas
- **Prosperity:** 44 / 100
- **Governor Authority:** 35 / 100
- **Controlling faction:** England (nominal; enforcement is weak)
- **Garrison:** Small (1 sloop, 60 militia)
- **Facilities:** Chandler, Broker, Tavern, Shipwright (basic repairs only)
- **Economic profile:**
  - Produces: Dried fish, Wrecked cargo (salvage)
  - Demands: Provisions, Rum, Naval stores
  - Base price modifiers: Rum –20%, Provisions +15%
- **Notes:** The governor of Nassau is a political appointment by a distant colonial office — he has almost no practical authority. Smugglers, unemployed privateers, and small traders make up most of the port community. Knowledge here is cheap and plentiful but unreliable.

#### St. Augustine *(Spain — Naval Base)*
- **Port ID key:** `st_augustine`
- **Location:** Florida east coast, northernmost Spanish Caribbean port
- **Prosperity:** 30 / 100
- **Governor Authority:** 70 / 100
- **Controlling faction:** Spain
- **Garrison:** Medium (2 patrol vessels, 200 soldiers, Castillo de San Marcos)
- **Facilities:** Chandler, Armoury, Church
- **Economic profile:**
  - Produces: Timber, Pitch
  - Demands: Food, Cloth, Salt
  - Base price modifiers: Timber –15%, Food +25%
- **Notes:** Primarily a garrison and forward outpost against English incursion from the north. Hostile to non-Spanish vessels; port access requires papers or a bribe. Trade is incidental — the port exists to project military presence.

#### Port Charlotte *(England — Fishing Port / Smuggling)*
- **Port ID key:** `port_charlotte`
- **Location:** Southwest Florida cays
- **Prosperity:** 22 / 100
- **Governor Authority:** 15 / 100
- **Controlling faction:** England (very loose)
- **Garrison:** None (a single armed customs sloop, irregularly present)
- **Facilities:** Chandler (basic), Tavern
- **Economic profile:**
  - Produces: Fish, Turtle shell, Salvaged goods
  - Demands: Rum, Cloth, Provisions
  - Base price modifiers: Fish –30%, Rum +20%
- **Notes:** A collection of fishing camps and seasonal traders rather than a true port. The "governor" is a local headman with a commission from Jamaica. Knowledge of shallow-water passage routes through the Bahamas is available here from experienced local pilots.

#### Baracoa *(Spain — Fishing Port)*
- **Port ID key:** `baracoa`
- **Location:** Eastern Cuba
- **Prosperity:** 28 / 100
- **Governor Authority:** 55 / 100
- **Controlling faction:** Spain
- **Garrison:** Small (60 soldiers, coastal battery)
- **Facilities:** Chandler, Church
- **Economic profile:**
  - Produces: Fish, Cacao, Honey
  - Demands: Salt, Tools, Cloth
  - Base price modifiers: Cacao –20%, Salt +30%
- **Notes:** Cuba's oldest European settlement; small, proud, and often overlooked by Havana's administration. Cacao is the local speciality — the trees here predate Spanish arrival. A useful resupply stop on the passage east.

---

### 4.2 Hispaniola Waters Ports

#### Port Royal *(England — Trade Hub / Colonial Capital)*
- **Port ID key:** `port_royal`
- **Location:** Southeast Jamaica
- **Prosperity:** 78 / 100
- **Governor Authority:** 58 / 100
- **Controlling faction:** England
- **Garrison:** Medium (4 warships, 300 soldiers, Fort Charles)
- **Facilities:** Shipyard (full), Chandler, Broker (2), Tavern (several), Armoury, Prison, Church, Fence
- **Economic profile:**
  - Produces: Sugar, Rum, Logwood
  - Demands: Manufactured goods, Spices, Enslaved labour
  - Base price modifiers: Rum –25% (high local production), Manufactured goods +35%
- **Notes:** The richest and most depraved city in the New World. Port Royal has an unofficial open-door policy for privateers and pirates — the governor takes a cut, the merchants take a cut, and no one asks too many questions. The two brokers here deal in everything: trade goods, intelligence, introductions, letters of marque. The fence ("Redcloak") is the best in the Caribbean. This will be Port Royal's prosperity peak; the earthquake of 1692 is in the future.

#### Santo Domingo *(Spain — Colonial Capital)*
- **Port ID key:** `santo_domingo`
- **Location:** Southern Hispaniola
- **Prosperity:** 55 / 100
- **Governor Authority:** 82 / 100
- **Controlling faction:** Spain
- **Garrison:** Large (3 warships, 500 soldiers, multiple fortifications)
- **Facilities:** Shipyard (full), Chandler, Broker, Armoury, Church (cathedral), Prison, Court
- **Economic profile:**
  - Produces: Cattle, Hides, Hardwood
  - Demands: Wine, Luxury goods, Weapons
  - Base price modifiers: Hides –20%, Wine +45%
- **Notes:** The oldest continuously occupied European settlement in the Americas. The governor here is a grandee of some standing in Madrid — appointments to Santo Domingo are prestigious but considered a backwater compared to Havana or Cartagena. The city is wealthy in prestige rather than commerce; its prosperity has been quietly declining as French Saint-Domingue grows. The governor is acutely aware of this.

#### Tortuga *(Brethren of the Coast — Pirate Haven)*
- **Port ID key:** `tortuga`
- **Location:** Small island, north coast of Hispaniola
- **Prosperity:** 51 / 100
- **Governor Authority:** 22 / 100 *(no formal authority; this score reflects the Brethren council's practical control)*
- **Controlling faction:** Brethren of the Coast
- **Garrison:** None formal (3-8 pirate vessels typically present; armed populace)
- **Facilities:** Chandler, Shipwright (good repairs, no new builds), Tavern (many), Fence (excellent), Broker (information only)
- **Economic profile:**
  - Produces: None (entrepôt for plunder)
  - Demands: Provisions, Rum, Gunpowder, Rope
  - Base price modifiers: Plunder goods –40% (buyer's market), Provisions +30%
- **Notes:** The pirate capital. The Brethren's loose governing council meets in the largest tavern (The Bleeding Anchor). Ships are careened on the beach by the western cove. The fence here (known as "Mother Vane") will buy almost anything and asks nothing. Knowledge flows freely — too freely; anything said here will be known by competing factions within a few ship departures.

#### Cap-Français *(France — Plantation Colony / Growing Capital)*
- **Port ID key:** `cap_francais`
- **Location:** Northern Saint-Domingue (Haiti), northern coast
- **Prosperity:** 62 / 100
- **Governor Authority:** 65 / 100
- **Controlling faction:** France
- **Garrison:** Medium (2 warships, 200 soldiers)
- **Facilities:** Chandler, Broker, Shipwright (basic), Church
- **Economic profile:**
  - Produces: Sugar, Indigo, Coffee (small amounts, emerging)
  - Demands: Manufactured goods, Enslaved labour, Tools
  - Base price modifiers: Indigo –15%, Manufactured goods +40%
- **Notes:** Saint-Domingue is becoming the jewel of the French Caribbean and the governor knows it. Cap-Français is growing quickly — the broker here is better informed about French colonial intentions than most. France is aggressively pushing development; the relationship between the French planters (who want slave labour and no taxes) and the Crown governor (who wants both) is a source of ongoing friction.

#### Port Antonio *(England — Plantation Colony)*
- **Port ID key:** `port_antonio`
- **Location:** Northeast Jamaica
- **Prosperity:** 38 / 100
- **Governor Authority:** 50 / 100
- **Controlling faction:** England
- **Garrison:** Small (60 militia, 1 small patrol vessel)
- **Facilities:** Chandler, Church
- **Economic profile:**
  - Produces: Sugar, Bananas, Logwood
  - Demands: Salt, Tools, Cloth
  - Base price modifiers: Logwood –20%, Salt +20%
- **Notes:** The quieter, more respectable face of Jamaica. The planters here are legitimate landowners who resent Port Royal's pirate connections. Information from here tends toward plantation economics and colonial politics rather than underworld gossip. Occasionally useful for legitimate cargo runs.

#### Léogâne *(France — Plantation Colony)*
- **Port ID key:** `leogane`
- **Location:** Southwest Hispaniola, Gulf of Gonâve
- **Prosperity:** 34 / 100
- **Governor Authority:** 48 / 100
- **Controlling faction:** France
- **Garrison:** Small (80 soldiers, 1 patrol sloop)
- **Facilities:** Chandler, Church
- **Economic profile:**
  - Produces: Sugar, Cotton, Hides
  - Demands: Food, Cloth, Rum
  - Base price modifiers: Cotton –10%, Rum +15%
- **Notes:** A modest but growing French settlement. The governor is a younger son of a minor noble — ambitious, short of funds, and receptive to creative arrangements. Sugar production is increasing; the demand for labour and tools creates a reliable trade route with Tortuga, much to the official disgust of the colonial administration in Cap-Français.

---

### 4.3 Leeward Chain Ports

#### Oranjestad *(Netherlands — Trade Hub)*
- **Port ID key:** `oranjestad`
- **Location:** Sint Eustatius (Statia)
- **Prosperity:** 70 / 100
- **Governor Authority:** 55 / 100
- **Controlling faction:** Netherlands (West India Company)
- **Garrison:** Small (80 WIC soldiers, 1 warship usually present)
- **Facilities:** Chandler (exceptional stock), Broker (3 — the most of any port), Shipwright (full), Warehouse district
- **Economic profile:**
  - Produces: None (pure entrepôt)
  - Demands: Everything — and sells everything
  - Base price modifiers: All goods within 10% of true market value; minimal price distortion
- **Notes:** "The Golden Rock." Sint Eustatius handles more trade tonnage than any port its size has any right to. The Dutch WIC enforces one rule only: pay your fees. Statia is officially neutral and the WIC will trade with Spain, England, France, and pirates simultaneously. The three brokers here collectively know the price, availability, and rough location of almost every significant cargo in the Caribbean. Information from Statia is expensive but reliable — the Dutch are meticulous record-keepers. Any faction can use this port; none control it.

#### St. John's *(England — Naval Base / Colonial Capital)*
- **Port ID key:** `st_johns`
- **Location:** Antigua
- **Prosperity:** 52 / 100
- **Governor Authority:** 72 / 100
- **Controlling faction:** England
- **Garrison:** Medium (3 warships, 200 soldiers, English Harbour fortification)
- **Facilities:** Shipyard (full — English Harbour is a proper naval dockyard), Chandler, Broker, Armoury, Church
- **Economic profile:**
  - Produces: Sugar, Rum, Naval stores
  - Demands: Provisions, Timber, Rope
  - Base price modifiers: Naval stores –20%, Provisions +20%
- **Notes:** English Harbour is the best natural anchorage in the Eastern Caribbean and the Royal Navy knows it. The naval base here is England's strategic fulcrum for the Leeward Islands. The governor is a naval officer, not a civilian, and runs a tighter establishment than Port Royal. Knowledge of English naval patrol schedules leaks from here — the navy's officers are not always discrete.

#### Basseterre *(France — Plantation Colony)*
- **Port ID key:** `basseterre`
- **Location:** St. Kitts (French northern half)
- **Prosperity:** 45 / 100
- **Governor Authority:** 60 / 100
- **Controlling faction:** France
- **Garrison:** Medium (150 soldiers, 1 warship)
- **Facilities:** Chandler, Church, Broker (trade goods focus)
- **Economic profile:**
  - Produces: Sugar, Tobacco
  - Demands: Manufactured goods, Enslaved labour, Salt
  - Base price modifiers: Sugar –15%, Manufactured goods +35%
- **Notes:** St. Kitts is the oldest French Caribbean colony and the French are proud of it. The island is divided between France (north) and England (south, see below) with a demilitarised strip between — a arrangement that produces constant low-level tension and occasional diplomatic incidents.

#### Sandy Point *(England — Plantation Colony)*
- **Port ID key:** `sandy_point`
- **Location:** St. Kitts (English southern half)
- **Prosperity:** 42 / 100
- **Governor Authority:** 55 / 100
- **Controlling faction:** England
- **Garrison:** Small (100 soldiers)
- **Facilities:** Chandler, Church
- **Economic profile:**
  - Produces: Sugar, Cotton
  - Demands: Tools, Cloth, Provisions
  - Base price modifiers: Cotton –10%, Tools +20%
- **Notes:** The English half of a divided island, sharing St. Kitts with the French. The proximity breeds both trade and friction; the local English governor and French governor have a testy but functional working relationship. Sandy Point is a useful mid-chain resupply.

#### Philipsburg *(Netherlands — Smuggling Post)*
- **Port ID key:** `philipsburg`
- **Location:** Sint Maarten (Dutch southern half — shared with France)
- **Prosperity:** 35 / 100
- **Governor Authority:** 30 / 100
- **Controlling faction:** Netherlands (nominally), effectively semi-autonomous
- **Garrison:** Token (30 WIC men, no warship)
- **Facilities:** Chandler, Tavern, Fence (small)
- **Economic profile:**
  - Produces:** Salt (Great Salt Pond — major Caribbean salt source)
  - Demands: Everything taxed elsewhere
  - Base price modifiers: Salt –50% (exceptional surplus), Contraband goods at fair prices
- **Notes:** Sint Maarten's salt pans are economically valuable but the island's real trade is the relaxed attitude of its customs officials. What Statia handles openly and legitimately, Philipsburg handles quietly and cheaply. A useful port for carrying goods that shouldn't be declared.

#### Marigot *(France — Fishing Port)*
- **Port ID key:** `marigot`
- **Location:** Saint-Martin (French northern half)
- **Prosperity:** 25 / 100
- **Governor Authority:** 45 / 100
- **Controlling faction:** France
- **Garrison:** Small (50 soldiers)
- **Facilities:** Chandler (basic), Church
- **Economic profile:**
  - Produces: Fish, Salt (shared access to Great Pond)
  - Demands: Provisions, Cloth, Rum
  - Base price modifiers: Fish –25%, Rum +20%
- **Notes:** The quieter, more formal French half of a shared island. The French commandant and Dutch governor of Philipsburg maintain the peculiar Treaty of Concordia — no border, free movement, mutual tolerance. Marigot is genuinely too small to be strategically significant but is a useful safe harbour in a storm.

---

### 4.4 Windward Reach Ports

#### Bridgetown *(England — Trade Hub / Plantation Capital)*
- **Port ID key:** `bridgetown`
- **Location:** Barbados, southwest coast
- **Prosperity:** 80 / 100
- **Governor Authority:** 75 / 100
- **Controlling faction:** England
- **Garrison:** Medium (3 warships, 250 soldiers, Needham's Fort and Charles Fort)
- **Facilities:** Shipyard (full), Chandler, Broker (2), Armoury, Church, Prison
- **Economic profile:**
  - Produces: Sugar (massive volume), Rum (massive volume), Cotton, Ginger
  - Demands: Enslaved labour, Manufactured goods, Provisions
  - Base price modifiers: Sugar –30% (floods market), Manufactured goods +50%
- **Notes:** The wealthiest English port in the Caribbean by output. Barbados is the sugar engine of the empire; the planters here are obscenely rich and politically influential. The governor of Barbados has more de facto autonomy than almost any other colonial governor — London is far away and the planters' lobby is powerful. Knowledge of English colonial trade policy is best sourced here. The brokers here are well-connected to London merchant houses.

#### Fort-de-France *(France — Naval Base / Colonial Capital)*
- **Port ID key:** `fort_de_france`
- **Location:** Martinique, southwest coast
- **Prosperity:** 65 / 100
- **Governor Authority:** 78 / 100
- **Controlling faction:** France
- **Garrison:** Large (4 warships, 350 soldiers, multiple shore batteries)
- **Facilities:** Shipyard (full), Chandler, Broker, Armoury, Church
- **Economic profile:**
  - Produces: Sugar, Rum, Cacao
  - Demands: Naval stores, Weapons, Provisions
  - Base price modifiers: Cacao –15%, Naval stores +30%
- **Notes:** The seat of French power in the southern Caribbean. The governor is a senior naval officer reporting directly to the Minister of Marine; this port is less economically oriented than Bridgetown and more strategically oriented. France is building toward a position of regional dominance and Fort-de-France is the staging point. The naval commander here has ambitions of his own.

#### St. George's *(England — Plantation Colony)*
- **Port ID key:** `st_georges`
- **Location:** Grenada, southwest
- **Prosperity:** 48 / 100
- **Governor Authority:** 62 / 100
- **Controlling faction:** England
- **Garrison:** Small (100 soldiers, 1 patrol sloop)
- **Facilities:** Chandler, Church, Broker (spice trade)
- **Economic profile:**
  - Produces: Spices (nutmeg, mace, cloves — emerging), Cacao, Sugar
  - Demands: Tools, Cloth, Food
  - Base price modifiers: Spices –20% (local surplus), Tools +25%
- **Notes:** Grenada is younger and less developed than Barbados but the spice cultivation is producing returns that have caught the attention of merchants in London. The local broker focuses on the spice trade and has good contacts into the Leeward Chain. England and France have both contested Grenada in the past; the current English control is not taken entirely for granted.

#### Scarborough *(England — Contested / Fishing Port)*
- **Port ID key:** `scarborough`
- **Location:** Tobago, southwest
- **Prosperity:** 22 / 100
- **Governor Authority:** 25 / 100
- **Controlling faction:** England (recently seized from Netherlands)
- **Garrison:** Small (60 soldiers — under-resourced)
- **Facilities:** Chandler (basic), Tavern
- **Economic profile:**
  - Produces: Fish, Hardwood, Cacao
  - Demands: Provisions, Weapons, Tools
  - Base price modifiers: Hardwood –15%, Weapons +35%
- **Notes:** Tobago has changed hands so many times (Courland, Netherlands, England, France) that the local population barely registers each new flag. The current English garrison is under-supplied and the governor is quietly hoping for a transfer. The Dutch WIC and French Marine both have unresolved claims. This port can shift controlling faction relatively easily — it is a natural flashpoint for diplomatic incidents between England, France, and Netherlands.

#### The Resting Place *(Black Company — Pirate Haven)*
- **Port ID key:** `the_resting_place`
- **Location:** An unnamed cay, southern Windward Reach (approximate location known; exact harbour entrance is not on any official chart)
- **Prosperity:** 38 / 100
- **Governor Authority:** 40 / 100 *(reflects Black Company council's functional command discipline — higher than Tortuga's chaos)*
- **Controlling faction:** Black Company
- **Garrison:** None formal (typically 2-4 Company vessels; the anchorage is defended by knowledge of its location being restricted)
- **Facilities:** Chandler (limited), Shipwright (careening and hull work), Fence (selective — the Company is choosy), Tavern (one)
- **Economic profile:**
  - Produces: None
  - Demands: Provisions, Gunpowder, Rope
  - Base price modifiers: Plunder goods –35%, Provisions +40%
- **Notes:** Unlike Tortuga's chaotic market, the Resting Place runs on discipline. The Black Company operates more like a mercenary company than a pirate band — captains answer to a council, shares are calculated precisely, and information is treated as a resource to be hoarded. Access requires introduction or demonstrable mutual interest; strangers are not welcome. The port's location is the highest-value piece of knowledge in the Windward Reach; factions that learn it will act on it.

---

### 4.5 Spanish Main Ports

#### Cartagena de Indias *(Spain — Treasure Port / Colonial Capital)*
- **Port ID key:** `cartagena`
- **Location:** Colombian coast, protected bay
- **Prosperity:** 75 / 100
- **Governor Authority:** 90 / 100
- **Controlling faction:** Spain
- **Garrison:** Massive (6+ warships, 1,000+ soldiers, Castillo San Felipe de Barajas, multiple sea forts, chain boom across harbour mouth)
- **Facilities:** Shipyard (full), Chandler, Broker (official only), Armoury, Church (cathedral), Prison (the worst in the Caribbean)
- **Economic profile:**
  - Produces: Emeralds, Silver (transit), Gold (transit), Leather, Dyes
  - Demands: Everything — this is a consuming city, not a producing one
  - Base price modifiers: Emeralds at world market price (controlled); Luxury goods +60%
- **Notes:** The most heavily fortified city in the Americas. The harbour entrance is controlled by a chain boom and covered by cross-fire from multiple shore batteries. No hostile force has ever successfully taken Cartagena by sea assault and Spain intends to keep it that way. Non-Spanish vessels may enter under strict conditions — trade papers, declared cargo, escorted transit. The governor is the most powerful individual in the Caribbean; his authority score reflects genuine command, not wishful thinking. Knowledge of treasure fleet assembly here is priceless and Spain protects it accordingly.

#### Willemstad *(Netherlands — Trade Hub)*
- **Port ID key:** `willemstad`
- **Location:** Curaçao, protected harbour
- **Prosperity:** 68 / 100
- **Governor Authority:** 62 / 100
- **Controlling faction:** Netherlands (WIC)
- **Garrison:** Medium (2 warships, 150 WIC soldiers, Fort Amsterdam)
- **Facilities:** Shipyard (full), Chandler, Broker (2), Armoury, Slave market
- **Economic profile:**
  - Produces: Salt, Divi-divi (tannin), Horses (re-exported to colonies)
  - Demands: Everything the Spanish Main produces
  - Base price modifiers: Horses –20%, Spanish goods at premium (contraband markup)
- **Notes:** The Dutch gateway to the Spanish Main. Curaçao has a special and somewhat awkward status — officially neutral and a WIC trade post, it is in practice the primary conduit for contraband trade with Spanish colonial ports. Spanish merchants who need goods not available through the official Flota system send agents to Willemstad. The two brokers here have deep contacts into Spanish colonial commerce and are among the best-informed people in the Caribbean about Spanish Main affairs. The slave market is the largest in the Caribbean — a grim practical reality of the era.

#### Maracaibo *(Spain — Treasure Port)*
- **Port ID key:** `maracaibo`
- **Location:** Gulf of Venezuela, Lake Maracaibo entrance
- **Prosperity:** 58 / 100
- **Governor Authority:** 70 / 100
- **Controlling faction:** Spain
- **Garrison:** Medium (2 warships, 300 soldiers, bar forts at lake entrance)
- **Facilities:** Chandler, Broker, Armoury, Church
- **Economic profile:**
  - Produces: Pearls (nearby fishing banks), Cacao, Hides, Oil (seep — minor)
  - Demands: Manufactured goods, Wine, Provisions
  - Base price modifiers: Pearls at high premium when available, Cacao –20%
- **Notes:** The pearl fisheries around the Margarita islands and the Maracaibo approaches produce the most sought-after pearls in the Spanish empire. The lake access is controlled by twin forts at the bar; the channel is shallow enough that large warships cannot enter fully laden. The governor here is prosperous and somewhat corrupt — willing to negotiate with the right kind of visitor if the meeting is discreet. Morgan sacked this city in 1669; the governor has not forgotten.

#### Portobelo *(Spain — Treasure Port / Naval Base)*
- **Port ID key:** `portobelo`
- **Location:** Panamanian coast, eastern entrance to the Isthmus
- **Prosperity:** 45 / 100
- **Governor Authority:** 80 / 100 *(during Feria periods); 50 (between)*
- **Controlling faction:** Spain
- **Garrison:** Large during Feria (4+ warships, 600 soldiers); skeleton between fairs
- **Facilities:** Chandler, Armoury, Warehouses (massive), Church
- **Economic profile:**
  - Produces: Peruvian silver (transit only, cyclically)
  - Demands: European manufactured goods (during Feria only)
  - Base price modifiers: Extreme during Feria (silver surplus, goods shortage); near-ghost-town between
- **Notes:** Portobelo exists almost entirely for the Feria — the great fair held when the silver fleet arrives from Peru. For weeks Portobelo becomes the richest place in the world; between fairs it is a small, hot, disease-ridden garrison. The timing of the Feria is a state secret Spain guards carefully. A captain who knows when the silver arrives and has the audacity to act on that knowledge... but the fortifications are formidable. Drake tried; he died trying.

#### Port of Spain *(Spain — Plantation Colony / Contested)*
- **Port ID key:** `port_of_spain`
- **Location:** Trinidad, northwest coast
- **Prosperity:** 35 / 100
- **Governor Authority:** 52 / 100
- **Controlling faction:** Spain
- **Garrison:** Small (100 soldiers, 1 patrol vessel)
- **Facilities:** Chandler, Church
- **Economic profile:**
  - Produces: Tobacco, Cacao, Pitch (natural asphalt — unique)
  - Demands: Tools, Cloth, Food
  - Base price modifiers: Pitch –25% (unique local surplus), Tools +30%
- **Notes:** Trinidad is Spain's southernmost significant Caribbean possession, sitting at the mouth of the Orinoco delta. The natural pitch lake (La Brea) produces ship's caulking material that is genuinely sought after — a small but real edge in ship maintenance. The governor is underfunded and undersupported; Trinidad feels abandoned by Cartagena. England and the Netherlands have both made moves on Trinidad; it sits in the tension zone between colonial spheres.

#### Santa Marta *(Spain — Plantation Colony)*
- **Port ID key:** `santa_marta`
- **Location:** Colombian coast, west of Cartagena
- **Prosperity:** 30 / 100
- **Governor Authority:** 65 / 100
- **Controlling faction:** Spain
- **Garrison:** Small (80 soldiers)
- **Facilities:** Chandler (basic), Church
- **Economic profile:**
  - Produces: Emeralds (minor — Muzo mines connection), Hides, Hardwood
  - Demands: Provisions, Cloth, Wine
  - Base price modifiers: Emeralds at variable price, Provisions +25%
- **Notes:** The oldest Spanish settlement in continental South America, now somewhat overshadowed by Cartagena. The governor here resents Cartagena's precedence. Some contraband flows through Santa Marta to avoid Cartagena's inspections — the governor's resentment makes him pragmatic about certain arrangements.

---

## 5. Factions

### 5.1 Spain — Armada de Barlovento

**Faction ID key:** `spain`
**Type:** Colonial
**Colour identity:** Gold and crimson

**Identity and motivation:**
Spain believes the Caribbean is theirs by Papal grant and right of conquest. The treasure flowing through Cartagena and Portobelo funds the Habsburg empire; protecting it is not merely an economic imperative but a theological and dynastic one. Spain's Caribbean policy is defensive — hold what they have, suppress piracy, and ensure the silver reaches Seville. Expansion at this point is less important than preservation.

**Starting state:**
- Treasury: 4,200 reales (high — regular treasure fleet income)
- Naval Strength: 82 / 100 (dominant at start)
- Patrol Allocation: 35% of naval strength assigned to patrol
- Desired Patrol Allocation: 40%
- Ports controlled: Havana, St. Augustine, Baracoa, Santo Domingo, Cartagena, Maracaibo, Portobelo, Port of Spain, Santa Marta (9 ports — largest holding)

**Initial goals (by utility score):**
1. `SuppressPiracy` (utility 0.78) — Brethren raid Spanish shipping consistently
2. `ProtectTradeRoute` — Florida Channel (utility 0.72) — treasure fleet vulnerability
3. `MaintenanceGoal` — Cartagena fortifications (utility 0.65)

**Personality traits (for LLM prompt injection):**
- Formal, legalistic, proud — governors invoke Spanish law and Papal authority
- Slow to act but massive when mobilised
- Deeply suspicious of Dutch trade with Spanish colonists
- Will negotiate with England in extremis; will not negotiate with pirates

**Special mechanic — Treasure Fleet:**
Twice per in-game year (roughly April–May and August–September), a Treasure Fleet event sequence occurs: Portobelo Feria is activated, a fleet assembles in Havana, and silver shipments move through the Florida Channel. This is simulatable as a PendingFactionStimulus sequence; knowledge of the schedule is the most valuable intelligence in the game.

---

### 5.2 England — Royal Navy Caribbean Squadron

**Faction ID key:** `england`
**Type:** Colonial
**Colour identity:** Red and blue

**Identity and motivation:**
England's Caribbean strategy is opportunistic and commercially minded. The Crown wants trade revenue and strategic position; the planters want cheap labour and open markets; the merchants want access to Spanish trade. These interests conflict. England is the most willing of the colonial powers to use privateers and operate in legal grey zones — a habit that makes them useful to the player but unpredictable.

**Starting state:**
- Treasury: 2,800 pounds (moderate)
- Naval Strength: 58 / 100
- Patrol Allocation: 28%
- Desired Patrol Allocation: 30%
- Ports controlled: Port Royal, Port Antonio, St. John's, Basseterre (shared), Sandy Point, Bridgetown, St. George's, Scarborough, Nassau (nominally) (8 ports)

**Initial goals (by utility score):**
1. `ExpandTradeRoute` — Leeward Chain to Spanish Main (utility 0.70)
2. `SuppressPiracy` (utility 0.55) — lower priority than Spain because Port Royal benefits from pirate presence
3. `BuildNavalStrength` (utility 0.60)

**Personality traits:**
- Commercially pragmatic — everything has a price
- Conflicted about piracy (privateering background; Port Royal dependency)
- Institutionally suspicious of France; personally contemptuous of Spain
- Responsive to bribes through proper channels (called "gifts to the Crown")

---

### 5.3 France — Marine Royale, Caribbean Station

**Faction ID key:** `france`
**Type:** Colonial
**Colour identity:** White and blue

**Identity and motivation:**
France under Louis XIV is expansionist and prestige-conscious. The Caribbean colonies are a demonstration of French glory as much as a commercial enterprise. The governor of Fort-de-France has the ear of the Minister of Marine; French colonial policy is more centrally directed than England's mercantile chaos. France is building systematically toward dominance of the Lesser Antilles and sees Saint-Domingue as the future crown jewel.

**Starting state:**
- Treasury: 3,100 livres (moderate-high)
- Naval Strength: 62 / 100
- Patrol Allocation: 30%
- Desired Patrol Allocation: 35%
- Ports controlled: Cap-Français, Léogâne, Basseterre (shared), Marigot, Fort-de-France (5 ports)

**Initial goals (by utility score):**
1. `ExpandInfluence` — Windward Reach (utility 0.75) — Tobago and St. Vincent are targets
2. `BuildNavalStrength` (utility 0.68)
3. `ProtectTradeRoute` — sugar convoys (utility 0.60)

**Personality traits:**
- Formal and status-conscious — rank matters in every interaction
- Long strategic horizon — France is patient and plans in decades
- Distrustful of the Dutch (commercial competition)
- Willing to use pirates against Spain but not against England (diplomatic calculation)

---

### 5.4 Netherlands — West India Company (WIC)

**Faction ID key:** `netherlands`
**Type:** Colonial
**Colour identity:** Orange and white

**Identity and motivation:**
The Dutch WIC is not a state — it is a company with a flag. Its primary interest is trade, not territory. The WIC will deal with anyone, hold territory only where it is commercially necessary, and avoid military confrontation wherever possible. The Dutch naval weakness after the Anglo-Dutch Wars means the WIC runs on diplomacy and commercial leverage rather than force. Sint Eustatius and Curaçao are the two pillars of Dutch Caribbean strategy; both are trade posts, not fortresses.

**Starting state:**
- Treasury: 3,800 guilders (high — best commercial returns of any faction)
- Naval Strength: 35 / 100 (lowest of colonial powers — deliberately so)
- Patrol Allocation: 20% (primarily trade escort, not combat patrol)
- Desired Patrol Allocation: 20%
- Ports controlled: Oranjestad, Philipsburg (nominally), Willemstad (3 ports — fewest, highest value)

**Initial goals (by utility score):**
1. `ExpandTradeRoute` — Spanish Main contraband access (utility 0.80)
2. `MaintainNeutrality` — custom goal: stay out of colonial wars (utility 0.75)
3. `BuildTreasuryReserve` (utility 0.65)

**Personality traits:**
- Commercially transactional — every relationship is a balance sheet
- Scrupulously neutral in colonial conflicts; takes offence at being dragged in
- Excellent intelligence network (commercial information is WIC's core business)
- Will bribe, negotiate, and trade with pirates but will not shelter them in WIC ports

---

### 5.5 Brethren of the Coast

**Faction ID key:** `brethren`
**Type:** Pirate
**Colour identity:** Black and red

**Identity and motivation:**
The Brethren are the original Caribbean pirates — former privateers, escaped indentured servants, deserters, and adventurers who found that the end of official privateering commissions left them with skills, ships, and no legal income. The Brethren operate on a charter system: each crew votes on its captain and articles, shares are divided by democratic agreement, and the loose council at Tortuga coordinates only when a common threat emerges. They are chaotic by design.

**Starting state:**
- Treasury: 820 pieces of eight (low — distributed to crews regularly)
- Naval Strength: 45 / 100
- RaidingMomentum: 60 / 100 (high — recent successful operations)
- HavenPresence: 70 / 100 (Tortuga is secure)
- Cohesion: 55 / 100 (workable but not unified)
- Ports controlled: Tortuga

**Initial goals (by utility score):**
1. `RaidShipping` — Spanish targets (utility 0.82) — ideological as much as economic
2. `MaintainHaven` — Tortuga (utility 0.70)
3. `RecruitCrew` (utility 0.55) — always short of experienced hands

**Personality traits:**
- Democratic and anti-authoritarian — captains propose, crews decide
- Deeply anti-Spanish (historical grievances; most members are English or French)
- Will work with England informally; hostile to Netherlands (commercial enemies)
- Fragile unity — Cohesion fractures under sustained pressure
- Loud, visible, bad at keeping secrets (knowledge from Tortuga spreads fast)

**Active notable captains at start:**
- **Bartholomew "Red Bel" Alcott** — most successful current captain; English, charismatic, pragmatic. Currently at sea (Hispaniola Waters). IDs as individual NPC.
- **Marie-Claire Dumont** — French, runs a fast sloop out of Tortuga; specialises in intelligence and ransom over violence. Influential in Brethren council discussions.

---

### 5.6 The Black Company

**Faction ID key:** `black_company`
**Type:** Pirate
**Colour identity:** Black and gold

**Identity and motivation:**
The Black Company was founded ten years before the game starts by a former naval officer who believed piracy could be run like a professional military unit. Where the Brethren celebrate chaos, the Black Company enforces discipline. Captains are appointed, not elected; shares are contractual; information is treated as property and protected accordingly. The Company takes selective targets — primarily merchant vessels carrying high-value goods — and avoids the kind of random brutality that brings navies down on pirate havens.

The result is a more durable, harder-to-eradicate organisation that colonial powers find frustrating: they can't simply raid its harbour because they can't easily find it, and they can't negotiate with it because the Company doesn't need their recognition.

**Starting state:**
- Treasury: 1,650 pieces of eight (moderate — better financial discipline than Brethren)
- Naval Strength: 38 / 100
- RaidingMomentum: 45 / 100 (selective raiding, lower frequency)
- HavenPresence: 55 / 100 (hidden haven is secure but small)
- Cohesion: 75 / 100 (high — disciplined organisation)
- Ports controlled: The Resting Place

**Initial goals (by utility score):**
1. `RaidShipping` — high-value merchant targets (utility 0.75)
2. `HoardIntelligence` — custom goal: acquire and protect strategic knowledge (utility 0.70)
3. `ExpandHavenPresence` (utility 0.55)

**Personality traits:**
- Disciplined, analytical, strategic — the Company plans operations in advance
- Will negotiate with colonial powers for specific mutual interests (not ideological)
- Treats information as currency; will trade intelligence with the player under the right conditions
- Retaliates precisely and severely against betrayal

**Active notable individuals at start:**
- **The Commodore** (real name unknown) — Company's founder and operational commander. Never seen at the Resting Place; communicates through intermediaries. A decision request candidate for LLM-driven behaviour.

---

## 6. Starting Faction Relationships

Relationships are expressed as a standing score from –100 (war) to +100 (alliance), with thresholds at –50 (hostile), –20 (tense), 0 (neutral), +20 (cordial), +50 (allied).

### Colonial-to-Colonial

| | Spain | England | France | Netherlands |
|---|---|---|---|---|
| **Spain** | — | –45 | –20 | –35 |
| **England** | –45 | — | –30 | +10 |
| **France** | –20 | –30 | — | –15 |
| **Netherlands** | –35 | +10 | –15 | — |

**Notes:**
- Spain/England at –45: officially ended hostilities (Treaty of Windsor 1670) but deep mutual suspicion; English privateers still active; Spain's navy will harass English shipping in disputed waters
- Spain/France at –20: complex — sometimes allied against England, but competing for Hispaniola; currently tense but not actively hostile
- Spain/Netherlands at –35: legacy of the Eighty Years' War; Dutch contraband trade with Spanish colonists is a constant irritant
- England/France at –30: colonial competition across the Caribbean; currently below the threshold where conflict is likely but flashpoints exist (Tobago, St. Kitts)
- England/Netherlands at +10: post Anglo-Dutch Wars cautious peace; commercial rivalry but no current cause for conflict
- France/Netherlands at –15: commercial competition in the Lesser Antilles; no active hostility

### Colonial-to-Pirate

| | Brethren | Black Company |
|---|---|---|
| **Spain** | –80 | –65 |
| **England** | –40 | –55 |
| **France** | –50 | –60 |
| **Netherlands** | –55 | –45 |

**Notes:**
- Spain is most hostile to Brethren (ideological — the Brethren specifically target Spanish shipping)
- England at –40 to Brethren: Port Royal's economic dependence on pirate spending creates institutional tolerance; official policy says hostile, actual enforcement is selective
- Netherlands at –45 to Black Company: the Company has been disciplined about not targeting Dutch merchants; this is the highest colonial-pirate starting score

### Pirate-to-Pirate

| | Brethren | Black Company |
|---|---|---|
| **Brethren** | — | +15 |
| **Black Company** | +15 | — |

**Notes:**
- Mildly positive: different operating areas, occasional mutual assistance, no territorial conflict. Not allies, but not enemies.

---

## 7. Named Governor Pool at World-Start

These are the Individuals present as port governors at world-start. Each has a brief personality profile sufficient to seed ActorDecisionMatrix generation.

| Name | Port | Faction | Background | Personality Notes |
|---|---|---|---|---|
| **Don Rodrigo de Velasco** | Cartagena | Spain | Aristocrat, career soldier, 58 | Rigid, incorruptible, meticulous; will invoke regulations to obstruct; responds to formal appeals through proper channels |
| **Don Alonso Prieto** | Havana | Spain | Naval officer, 52 | Pragmatic military man; can be persuaded if framed as strategic necessity; loathes paperwork, delegates to corrupt subordinates |
| **Capitán Luis Garza** | Maracaibo | Spain | Former privateer, turned establishment, 45 | Cynical, experienced, quietly corrupt; has seen everything; price is always right, just needs to be right |
| **Colonel James Hartley** | Port Royal | England | Soldier-administrator, 48 | Ambitious, politically connected; knows Port Royal's economy depends on piracy but wants promotion to a "respectable" post; torn |
| **Governor William Faraday** | Bridgetown | England | Planter-class, 61 | Old money, comfortable, wants peace and profit; respects commercial argument above all; deeply conservative |
| **Captain Robert Clough** | St. John's | England | Naval officer, 44 | Professionally excellent, politically naive; follows orders literally; responds well to hierarchy and navy protocols |
| **Monsieur Henri Vautrin** | Fort-de-France | France | Marine officer, 50 | Cultured, status-conscious, long-view thinker; will not be rushed; expects deference but rewards patience |
| **Gouverneur Pierre Leclerc** | Cap-Français | France | Colonial bureaucrat, 46 | Ambitious and frustrated; Cap-Français should be the jewel of France's empire but isn't yet; responsive to anything that advances the colony |
| **Directeur Pieter van Horn** | Oranjestad | Netherlands | WIC career man, 55 | Pure commercial calculation; everything is a negotiation; no moral objection to anything legal under WIC charter — and some illegal things |
| **Bewindvoerder Jan de Ruyter** | Willemstad | Netherlands | WIC merchant-officer, 49 | Methodical, record-keeping obsessive; knows the price of everything on the Spanish Main; a supreme information broker if approached correctly |
| **"Governor" Thomas Rake** | Nassau | England | Political appointment, former solicitor, 40 | In over his head; frightened; desperately wants to look competent to London; can be manipulated by anyone with a plausible plan |

---

## 8. Starting Economic Conditions

### Base Commodity Prices (in pieces of eight per unit)

| Commodity | Base Price | Notes |
|---|---|---|
| Sugar (barrel) | 8 | Caribbean staple; regional variation significant |
| Rum (cask) | 6 | Produced from sugar molasses; high regional surplus at production ports |
| Tobacco (bale) | 12 | Trinidad and smaller colonies; English Virginia competition off-map |
| Cotton (bale) | 9 | Leeward Chain speciality |
| Indigo (chest) | 22 | Dye; French colonies dominate |
| Cacao (bag) | 14 | Cuba, Grenada, Maracaibo |
| Spices (chest) | 35 | Grenada emerging; high value |
| Hides (bale) | 7 | Spanish Main cattle ranches |
| Hardwood (log) | 5 | Ship building; Jamaica and Main |
| Salt (barrel) | 3 | Philipsburg surplus; essential preservative |
| Fish (barrel) | 4 | Local production; spoils on long routes |
| Silver (ingot) | 50 | Spanish Main only; high regulation |
| Pearls (lot) | 40 | Maracaibo region; variable |
| Emeralds (lot) | 60 | Cartagena/Santa Marta; rare |
| Manufactured goods (case) | 18 | European origin; all colonies demand |
| Wine (cask) | 15 | Spanish/French colonies; European import |
| Provisions (barrel) | 5 | Food and water; universal need |
| Gunpowder (keg) | 20 | Military demand; restricted in Spanish ports |
| Rope (coil) | 4 | Ship maintenance consumable |
| Timber/naval stores (lot) | 8 | Ship building and maintenance |
| Enslaved people (individual) | 25 | Grim historical reality; major colonial trade |
| Contraband (mixed case) | 16 | Anything taxed heavily elsewhere; variable composition |

### Regional Price Modifiers at Start

Applied multiplicatively over base prices. Reflects initial production/consumption balance before simulation begins adjusting.

| Region | Produces at Surplus | In Demand |
|---|---|---|
| Florida Channel | Ship components, Provisions (Cuban agriculture) | Manufactured goods, Luxury goods |
| Hispaniola Waters | Sugar, Rum (Jamaica/Saint-Domingue), Logwood | Tools, Enslaved people, Manufactured goods |
| Leeward Chain | Sugar, Cotton, Salt (Statia/Sint Maarten) | Provisions, Manufactured goods |
| Windward Reach | Sugar (Barbados massive surplus), Rum, Spices | Labour, Manufactured goods |
| Spanish Main | Silver, Pearls, Emeralds, Hides, Cacao | European goods, Wine, Food |

### Trade Route Value Assessment

| Route | Leg 1 | Leg 2 | Estimated Profit Margin | Risk Level |
|---|---|---|---|---|
| Sugar Triangle | Bridgetown→Oranjestad (sell sugar) | Oranjestad→Bridgetown (buy manufactured goods) | Moderate | Low–Medium |
| Spanish Contraband | Willemstad→Santa Marta/Maracaibo (sell European goods) | Return with Hides/Pearls | High | High (Spanish patrol) |
| Rum Run | Port Royal→Tortuga (sell rum at surplus; buy information) | Tortuga→Nassau (sell information, buy salvage) | Low–Moderate | Low |
| Spice Premium | St. George's→Oranjestad (sell spices at premium) | Oranjestad→Fort-de-France (sell manufactured goods) | Good | Low–Medium |
| Silver Interception | Wait for Portobelo Feria intel → Florida Channel intercept | N/A | Extreme if successful | Extreme |

---

## 9. Starting Ships (Named Vessels)

These ships exist in the world at start and serve as reference points — some player-selectable as their own vessel, some as notable NPC vessels. Each has a full Ship entity in WorldState.

### Player-Selectable Starting Ships

| Name | Class | Condition | Current Location | Notes |
|---|---|---|---|---|
| *The Wayward Son* | Brigantine | 75/100 | Port Royal (docked) | Former merchant, recently sold; reliable, unspectacular; good starting ship |
| *La Dague* | Sloop | 85/100 | Tortuga (docked) | Fast, lightly armed; French-built; suits an information/trade-focused captain |
| *San Benedito* | Fluyt | 90/100 | Willemstad (docked) | Dutch cargo carrier; maximum trade capacity; minimal armament; WIC surplus sale |

### Notable NPC Vessels at Start

| Name | Class | Owner | Current Location | Notes |
|---|---|---|---|---|
| *Red Fortune* | Brigantine | Bartholomew Alcott (Brethren) | At sea, Hispaniola Waters | Alcott's command; well-armed, reputation precedes it |
| *L'Oiseau Noir* | Sloop | Marie-Claire Dumont (Brethren) | Tortuga (docked) | Fast courier/ransom vessel |
| *HMS Steadfast* | Fourth-rate | Royal Navy (Clough) | St. John's | English naval patrol; Leeward Chain |
| *La Vengeance Royale* | Frigate | Marine Royale | Fort-de-France | French naval patrol; Windward Reach |
| *Nuestra Señora del Mar* | Galleon | Spain (treasure fleet) | Portobelo (loading) | Silver transport; departs with Feria completion |

---

## 10. Open Questions

1. **Portobelo Feria scheduling** — Should the first Feria occur at a fixed offset from start (e.g., April 1680) or be randomised within a window? Fixed is more predictable for first-playthrough design; randomised creates replay variety. Recommendation: fixed for first implementation, configurable later.

2. **Player starting region** — Hispaniola Waters is recommended as the default starting region based on density of interesting ports and faction complexity. Should there be alternative start positions (e.g., Spanish Main for a merchant-focused start, Leeward Chain for a trade-focused start)?

3. **Individual NPC density** — The governor pool above covers 11 starting governors. Do we need additional named individuals at start (merchants, pirate captains, etc.) or is it better to generate them dynamically once the simulation runs for a few ticks?

4. **Slave trade representation** — Historically unavoidable in this era. The current approach (listed as a commodity, present in game economy) is the minimal historically-honest treatment. Should this be handled differently in terms of player interaction and mechanics?

5. **Portobelo fortification and assault** — The document implies Portobelo cannot be taken by sea assault. Should this be a hard rule (assault attempt always fails) or a very-high-difficulty check? The historical Drake failure and the later Morgan success suggest high difficulty rather than impossibility.

6. **The Commodore** — The Black Company leader is deliberately mysterious. Should their identity be fixed (a specific character the player eventually discovers) or procedurally generated per save? Fixed is better for designed narrative; procedural is better for replayability.
