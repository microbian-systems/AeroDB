module BlackJackFS.Domain

// ═══════════════════════════════════════════════════════════════
// Domain model using Discriminated Unions, Records, and Options
// ═══════════════════════════════════════════════════════════════

/// Card suits as a discriminated union
type Suit =
    | Hearts
    | Diamonds
    | Clubs
    | Spades
    override this.ToString() =
        match this with
        | Hearts -> "♥" | Diamonds -> "♦" | Clubs -> "♣" | Spades -> "♠"

/// Card ranks as a discriminated union with computed members
type Rank =
    | Ace
    | Two | Three | Four | Five | Six | Seven
    | Eight | Nine | Ten
    | Jack | Queen | King
    /// Primary value (Ace = 11 by default)
    member this.HighValue =
        match this with
        | Ace -> 11
        | Two -> 2 | Three -> 3 | Four -> 4 | Five -> 5
        | Six -> 6 | Seven -> 7 | Eight -> 8 | Nine -> 9
        | Ten | Jack | Queen | King -> 10
    /// Ace is special: can be 1 or 11
    member this.AceCard = match this with Ace -> true | _ -> false
    /// Face cards have value 10
    member this.IsFace = match this with Jack | Queen | King -> true | _ -> false
    /// Short display string
    override this.ToString() =
        match this with
        | Ace -> " A" | Two -> " 2" | Three -> " 3" | Four -> " 4"
        | Five -> " 5" | Six -> " 6" | Seven -> " 7" | Eight -> " 8"
        | Nine -> " 9" | Ten -> "10" | Jack -> " J" | Queen -> " Q"
        | King -> " K"

/// A single playing card (Record type)
type Card =
    { Suit: Suit
      Rank: Rank }
    override this.ToString() = $"[{this.Rank}{this.Suit}]"

/// Player action options (Discriminated Union)
type PlayerAction =
    | Hit
    | Stand
    | DoubleDown
    | Split
    member this.Display =
        match this with
        | Hit -> "(H)it" | Stand -> "(S)tand" | DoubleDown -> "(D)ouble" | Split -> "S(p)lit"

/// A hand of cards with its bet (Record type)
type Hand =
    { Cards: Card list
      Bet: int
      IsDoubledDown: bool
      IsStood: bool
      IsFromSplit: bool }
    static member Create bet =
        { Cards = []; Bet = bet; IsDoubledDown = false; IsStood = false; IsFromSplit = false }
    /// Has the hand finished its turn?
    member this.IsDone = this.IsBust || this.IsStood || this.IsDoubledDown
    /// Card point total (Aces counted as 1 when soft)
    member this.Score : int =
        let raw = this.Cards |> List.sumBy (fun c -> c.Rank.HighValue)
        let aceCount = this.Cards |> List.filter (fun c -> c.Rank.AceCard) |> List.length
        let rec reduce aces total =
            if aces = 0 || total <= 21 then total
            else reduce (aces - 1) (total - 10)
        reduce aceCount raw
    member this.IsBust = this.Score > 21
    member this.IsBlackjack =
        match this.Cards with
        | [a; b] when not this.IsDoubledDown ->
            (a.Rank.AceCard && b.Rank.HighValue = 10) ||
            (b.Rank.AceCard && a.Rank.HighValue = 10)
        | _ -> false
    /// Returns the soft/hard nature of the hand
    member this.IsSoft =
        let raw = this.Cards |> List.sumBy (fun c -> c.Rank.HighValue)
        let aceCount = this.Cards |> List.filter (fun c -> c.Rank.AceCard) |> List.length
        aceCount > 0 && raw <= 21

/// Game phases as a discriminated union (state machine)
type GamePhase =
    | Betting
    | Dealing
    | InsuranceOffer
    | PlayerTurn
    | DealerTurn
    | RoundComplete

/// Round outcome for a single hand (Discriminated Union)
type HandOutcome =
    | Blackjack
    | Win of byBust: bool
    | Lose of byBust: bool
    | Push
    override this.ToString() =
        match this with
        | Blackjack -> "Blackjack!"
        | Win true  -> "You win — dealer busts!"
        | Win false -> "You win!"
        | Lose true -> "Bust! You lose."
        | Lose false -> "Dealer wins."
        | Push -> "Push — it's a tie."

/// Player state (Record type)
type Player =
    { Name: string
      Chips: int
      CurrentBet: int option }
    static member Create name chips =
        { Name = name; Chips = chips; CurrentBet = None }

/// Complete game state (Record with Option fields)
type GameState =
    { Deck: Card list
      Player: Player
      PlayerHands: Hand list        // active hand queue; head = current hand
      CompletedHands: Hand list     // hands that finished their turn
      DealerHand: Hand
      Phase: GamePhase
      InsuranceTaken: bool
      DeckCount: int
      Message: string option
      RoundNumber: int }
