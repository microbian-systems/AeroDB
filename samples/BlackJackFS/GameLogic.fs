module BlackJackFS.GameLogic

open System
open BlackJackFS.Domain

// ═══════════════════════════════════════════════════════════════
// Pure game logic — no side effects, no I/O
// Railway-oriented with Result<'T, string>
// ═══════════════════════════════════════════════════════════════

// ── Deck operations ────────────────────────────────────────────

/// Generate a fresh, shuffled N-deck shoe (default 2, max 4)
let createDeck (deckCount: int) : Card list =
    let suits = [ Hearts; Diamonds; Clubs; Spades ]
    let ranks = [ Ace; Two; Three; Four; Five; Six; Seven;
                  Eight; Nine; Ten; Jack; Queen; King ]
    let singleDeck = [ for s in suits do for r in ranks -> { Suit = s; Rank = r } ]
    List.replicate (min 4 (max 1 deckCount)) singleDeck
    |> List.concat
    |> List.sortBy (fun _ -> Random.Shared.Next())

/// Draw one card: returns (card, remainingDeck)
let drawCard (deck: Card list) : Card * Card list =
    match deck with
    | [] -> failwith "Cannot draw from empty deck"
    | card :: rest -> (card, rest)

/// Draw N cards: returns (cards, remainingDeck)
let drawCards (count: int) (deck: Card list) : Card list * Card list =
    let rec loop n d acc =
        if n <= 0 then (List.rev acc, d)
        else
            let card, rest = drawCard d
            loop (n - 1) rest (card :: acc)
    loop count deck []

// ── Hand operations ────────────────────────────────────────────

/// Check if a hand can be split (exactly 2 cards with same rank value, player has chips)
let canSplit (hand: Hand) (state: GameState) : bool =
    match hand.Cards with
    | [a; b] -> a.Rank.HighValue = b.Rank.HighValue && state.Player.Chips >= hand.Bet
    | _ -> false

/// Split a hand into two: each gets one original card + one new card. Returns (hand1, hand2, remainingDeck)
let splitHand (hand: Hand) (deck: Card list) : Hand * Hand * Card list =
    match hand.Cards with
    | [a; b] ->
        let c1, deck1 = drawCard deck
        let c2, deck2 = drawCard deck1
        let h1 = { Hand.Create hand.Bet with Cards = [a; c1]; IsFromSplit = true }
        let h2 = { Hand.Create hand.Bet with Cards = [b; c2]; IsFromSplit = true }
        (h1, h2, deck2)
    | _ -> failwith "Cannot split hand without exactly 2 cards"

/// Dealer AI: should the dealer hit? (hit on soft 17 in this variant)
let dealerShouldHit (hand: Hand) : bool =
    let score = hand.Score
    // Hit on 16 or less, stand on hard 17+, hit on soft 17
    score < 17 || (score = 17 && hand.IsSoft)

// ── Basic Strategy Recommendation ──────────────────────────────

/// Basic strategy: given player's hand and dealer's up-card, recommend optimal play
let basicStrategy (hand: Hand) (dealerUpCard: Card) : string =
    let d = dealerUpCard.Rank.HighValue  // dealer's up-card value
    let score = hand.Score
    let soft = hand.IsSoft

    // Check for pairs first
    match hand.Cards with
    | [a; b] when a.Rank.HighValue = b.Rank.HighValue ->
        let pv = a.Rank.HighValue
        match pv with
        | 11 -> "Split"                              // A,A
        | 8  -> "Split"                              // 8,8
        | 10 -> "Stand"                              // 10,10 (never split tens)
        | 9 when d <> 7 && d <> 10 && d < 11 -> "Split"
        | 9  -> "Stand"
        | 7 when d <= 7 -> "Split"                   // 7,7 vs 2-7
        | 7  -> "Hit"
        | 6 when d <= 6 -> "Split"                   // 6,6 vs 2-6
        | 6  -> "Hit"
        | 4 when d = 5 || d = 6 -> "Split"           // 4,4 vs 5-6
        | 4  -> "Hit"
        | 2 | 3 when d <= 7 -> "Split"               // 2,2 3,3 vs 2-7
        | 2 | 3 -> "Hit"
        | 5  -> if d <= 9 then "Double Down" else "Hit"  // 5,5 → treat as 10
        | _  -> "Hit"
    | _ ->
    // Soft hands
    if soft then
        match score with
        | s when s >= 19 -> "Stand"
        | 18 when d >= 3 && d <= 6 -> "Double Down"
        | 18 when d = 2 || d = 7 || d = 8 -> "Stand"
        | 18 -> "Hit"
        | 17 when d >= 3 && d <= 6 -> "Double Down"
        | 17 -> "Hit"
        | 15 | 16 when d >= 4 && d <= 6 -> "Double Down"
        | 15 | 16 -> "Hit"
        | 13 | 14 when d = 5 || d = 6 -> "Double Down"
        | 13 | 14 -> "Hit"
        | _ -> "Hit"
    // Hard hands
    else
        match score with
        | s when s >= 17 -> "Stand"
        | 13 | 14 | 15 | 16 when d <= 6 -> "Stand"
        | 13 | 14 | 15 | 16 -> "Hit"
        | 12 when d >= 4 && d <= 6 -> "Stand"
        | 12 -> "Hit"
        | 11 when d = 11 -> "Hit"
        | 11 -> "Double Down"
        | 10 when d <= 9 -> "Double Down"
        | 10 -> "Hit"
        | 9 when d >= 3 && d <= 6 -> "Double Down"
        | 9 -> "Hit"
        | _ -> "Hit"

// ── Insurance ──────────────────────────────────────────────────

/// Check if player can afford insurance (half current bet)
let canBuyInsurance (state: GameState) : bool =
    let bet = state.Player.CurrentBet |> Option.defaultValue 0
    bet > 0 && state.Player.Chips >= bet / 2

/// Buy insurance: costs half the current bet
let buyInsurance (state: GameState) : GameState =
    let bet = state.Player.CurrentBet |> Option.defaultValue 0
    let cost = bet / 2
    { state with
        Player = { state.Player with Chips = state.Player.Chips - cost }
        InsuranceTaken = true
        Message = Some $"Insurance bought for {cost} chips." }

/// Check dealer blackjack after insurance decision
let checkDealerBlackjack (state: GameState) : GameState =
    let dealerHasBJ = state.DealerHand.IsBlackjack
    let playerHasBJ = (state.PlayerHands |> List.tryHead |> Option.map (fun h -> h.IsBlackjack)) = Some true
    if dealerHasBJ then
        { state with Phase = RoundComplete
                     Message = Some "Dealer has Blackjack!" }
    elif playerHasBJ then
        { state with Phase = DealerTurn; Message = Some "Blackjack!" }
    else
        { state with Phase = PlayerTurn }

// ── Outcome resolution ─────────────────────────────────────────

/// Resolve a single player hand vs dealer hand
let resolveHand (playerHand: Hand) (dealerHand: Hand) : HandOutcome =
    match playerHand.IsBlackjack, dealerHand.IsBlackjack with
    | true, true  -> Push
    | true, false -> Blackjack
    | false, true -> Lose false
    | _ ->
        match playerHand.IsBust, dealerHand.IsBust with
        | true, _      -> Lose true
        | _, true      -> Win true
        | _ ->
            match compare playerHand.Score dealerHand.Score with
            | c when c > 0 -> Win false
            | c when c < 0 -> Lose false
            | _            -> Push

/// Calculate payout for a hand outcome
let payout (hand: Hand) (outcome: HandOutcome) : int =
    match outcome with
    | Blackjack -> hand.Bet + (hand.Bet * 3 / 2)  // 3:2
    | Win _     -> hand.Bet * 2                     // 1:1
    | Push      -> hand.Bet                         // return bet
    | Lose _    -> 0                                // lose bet

// ── Action processing — railway-oriented ───────────────────────

/// Error type for invalid game actions
type GameError = string

/// Process a player action against the game state
let applyPlayerAction (action: PlayerAction) (state: GameState) : Result<GameState, GameError> =
    match state.Phase with
    | PlayerTurn ->
        let activeHand = state.PlayerHands.Head
        match action with
        | Hit ->
            match drawCard state.Deck with
            | card, remaining ->
                let updatedHand = { activeHand with Cards = activeHand.Cards @ [card] }
                Ok { state with Deck = remaining
                                PlayerHands = updatedHand :: state.PlayerHands.Tail
                                Phase = PlayerTurn
                                Message = Some $"You drew {card}" }
        | Stand ->
            Ok { state with PlayerHands = { activeHand with IsStood = true } :: state.PlayerHands.Tail
                            Phase = PlayerTurn
                            Message = Some "You stand." }

        | DoubleDown ->
            if activeHand.Cards.Length <> 2 then
                Error "Can only double down on first two cards."
            elif state.Player.Chips < activeHand.Bet then
                Error "Not enough chips to double down."
            else
                match drawCard state.Deck with
                | card, remaining ->
                    let updatedHand = { activeHand with
                                            Cards = activeHand.Cards @ [card]
                                            Bet = activeHand.Bet * 2
                                            IsDoubledDown = true }
                    let updatedPlayer = { state.Player with
                                              Chips = state.Player.Chips - activeHand.Bet }
                    Ok { state with Deck = remaining
                                    Player = updatedPlayer
                                    PlayerHands = updatedHand :: state.PlayerHands.Tail
                                    Phase = PlayerTurn
                                    Message = Some $"Double down! Drew {card}" }

        | Split ->
            if not (canSplit activeHand state) then
                Error "Cannot split this hand."
            else
                let h1, h2, remaining = splitHand activeHand state.Deck
                let updatedPlayer = { state.Player with Chips = state.Player.Chips - activeHand.Bet }
                Ok { state with
                       Deck = remaining
                       Player = updatedPlayer
                       PlayerHands = h1 :: h2 :: state.PlayerHands.Tail
                       Phase = PlayerTurn
                       Message = Some $"Split! Two hands of {activeHand.Bet} chips each." }
    | _ -> Error "Cannot perform action in current game phase."

/// Execute dealer's turn: hits until >= 17
let rec executeDealerTurn (state: GameState) : GameState =
    match state.Phase with
    | DealerTurn ->
        if dealerShouldHit state.DealerHand then
            let card, remaining = drawCard state.Deck
            let updatedDealer = { state.DealerHand with Cards = state.DealerHand.Cards @ [card] }
            executeDealerTurn { state with Deck = remaining
                                           DealerHand = updatedDealer
                                           Message = Some $"Dealer draws {card}" }
        else
            { state with Phase = RoundComplete
                         Message = Some $"Dealer stands with {state.DealerHand.Score}" }
    | _ -> state

// ── Initial deal ───────────────────────────────────────────────

/// Deal two cards each to player and dealer
let dealInitial (state: GameState) : GameState =
    let pCards, deck1 = drawCards 2 state.Deck
    let dCards, deck2 = drawCards 2 deck1
    let playerHand = { Hand.Create (state.Player.CurrentBet |> Option.defaultValue 0) with Cards = pCards }
    { state with Deck = deck2
                 PlayerHands = [playerHand]
                 DealerHand = { Hand.Create 0 with Cards = dCards }
                 Phase = PlayerTurn
                 Message = None }

// ── Initial state factory ──────────────────────────────────────

/// Create a fresh game state
let initGame (playerName: string) (chips: int) (deckCount: int) : GameState =
    let dc = min 4 (max 1 deckCount)  // clamp 1-4
    { Deck = createDeck dc
      Player = Player.Create playerName chips
      PlayerHands = []
      CompletedHands = []
      DealerHand = Hand.Create 0
      Phase = Betting
      InsuranceTaken = false
      DeckCount = dc
      Message = Some $"Welcome, {playerName}! You have {chips} chips. ({dc} deck(s))"
      RoundNumber = 1 }

// ── Betting ────────────────────────────────────────────────────

/// Place a bet. Returns Result.
let placeBet (amount: int) (state: GameState) : Result<GameState, GameError> =
    match state.Phase with
    | Betting ->
        if amount <= 0 then Error "Bet must be positive."
        elif amount > state.Player.Chips then Error $"Not enough chips. You have {state.Player.Chips}."
        else
            let updated = { state.Player with Chips = state.Player.Chips - amount
                                              CurrentBet = Some amount }
            Ok { state with Player = updated; Phase = Dealing; Message = Some $"Bet: {amount} chips" }
    | _ -> Error "Cannot bet in current phase."

/// Transition from dealing: check for dealer blackjack, offer insurance if dealer shows Ace
let afterDeal (state: GameState) : GameState =
    match state.Phase with
    | Dealing ->
        let state' = dealInitial state
        let dealerUpCard = state'.DealerHand.Cards.[1]  // second card is up-card
        let dealerHasBJ = state'.DealerHand.IsBlackjack
        let playerHasBJ = (state'.PlayerHands |> List.tryHead |> Option.map (fun h -> h.IsBlackjack)) = Some true

        if dealerUpCard.Rank.AceCard then
            // Dealer shows Ace → offer insurance before BJ check
            { state' with Phase = InsuranceOffer }
        elif dealerHasBJ then
            // Dealer has blackjack (non-Ace up card) → round over
            { state' with Phase = RoundComplete; Message = Some "Dealer has Blackjack!" }
        elif playerHasBJ then
            { state' with Phase = DealerTurn; Message = Some "Blackjack!" }
        else
            { state' with Phase = PlayerTurn }
    | _ -> state

/// Start a new round (reshuffle if needed)
let newRound (state: GameState) : GameState =
    let freshDeck = createDeck state.DeckCount
    { state with Deck = freshDeck
                 Player = { state.Player with CurrentBet = None }
                 PlayerHands = []
                 CompletedHands = []
                 DealerHand = Hand.Create 0
                 InsuranceTaken = false
                 Phase = Betting
                 Message = Some $"Round {state.RoundNumber + 1} — place your bet."
                 RoundNumber = state.RoundNumber + 1 }
