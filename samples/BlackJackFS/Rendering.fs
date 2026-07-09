module BlackJackFS.Rendering

open System
open BlackJackFS.Domain
open BlackJackFS.GameLogic

// ═══════════════════════════════════════════════════════════════
// Console rendering — ASCII cards, game state display
// ═══════════════════════════════════════════════════════════════

// ── Color helpers ──────────────────────────────────────────────

let private tryClear () =
    try Console.Clear()
    with :? System.IO.IOException -> printfn "\n---\n"

let private withColor (color: ConsoleColor) (action: unit -> unit) =
    let original = Console.ForegroundColor
    Console.ForegroundColor <- color
    action ()
    Console.ForegroundColor <- original

let private red ()    = withColor ConsoleColor.Red
let private cyan ()   = withColor ConsoleColor.Cyan
let private yellow () = withColor ConsoleColor.Yellow
let private green ()  = withColor ConsoleColor.Green
let private white ()  = withColor ConsoleColor.White
let private gray ()   = withColor ConsoleColor.DarkGray
let private magenta () = withColor ConsoleColor.Magenta

// ── ASCII card representation ──────────────────────────────────

/// Render a single card to ASCII art lines (array of 5 strings)
let private cardAscii (card: Card) (faceDown: bool) : string array =
    if faceDown then
        [| "┌─────┐"
           "│╱╲╱╲╱│"
           "│╲╱╲╱╲│"
           "│╱╲╱╲╱│"
           "└─────┘" |]
    else
        let top = sprintf "│%-2s   │" (card.Rank.ToString().Trim())
        let mid = sprintf "│  %s  │" (card.Suit.ToString())
        let bot = sprintf "│   %-2s│" (card.Rank.ToString().Trim())
        [| "┌─────┐"; top; mid; bot; "└─────┘" |]

/// Render a list of cards side by side
let private renderCards (cards: Card list) (faceDown: bool) (hideFirst: bool) : string list =
    match cards with
    | [] -> [ "   (empty)" ]
    | _ ->
        let asciis =
            cards
            |> List.mapi (fun i c ->
                let fd = if hideFirst && i = 0 then true else faceDown
                cardAscii c fd)
        [ for row in 0..4 ->
            asciis |> List.map (fun a -> a.[row]) |> String.concat " " ]

// ── Main rendering ─────────────────────────────────────────────

/// Print the full game state to console
let renderGame (state: GameState) (hideDealer: bool) =
    tryClear ()

    // Title bar
    magenta () (fun () ->
        printfn "╔══════════════════════════════════════╗"
        printfn "║          ♠ BLACKJACK ♥               ║"
        printfn "╚══════════════════════════════════════╝"
    )

    // Status line
    gray () (fun () ->
        printfn $"\nRound {state.RoundNumber} | Chips: {state.Player.Chips}"
        state.Player.CurrentBet |> Option.iter (fun b ->
            printf $" | Bet: {b}"
        )
    )

    // Message
    match state.Message with
    | Some msg ->
        yellow () (fun () -> printfn $"\n  {msg}")
    | None -> printfn ""

    // Dealer's hand
    printfn ""
    cyan () (fun () -> printfn "── Dealer ──")
    let dCards = renderCards state.DealerHand.Cards false hideDealer
    dCards |> List.iter (fun line -> printfn $"  {line}")
    if not hideDealer then
        let score = state.DealerHand.Score
        let soft = if state.DealerHand.IsSoft then " (soft)" else ""
        printfn $"  Score: {score}{soft}"

    // Player's hands
    let allHands = state.CompletedHands @ state.PlayerHands  // completed first, then active
    allHands |> List.iteri (fun i hand ->
        printfn ""
        let isActive = state.Phase = PlayerTurn && state.PlayerHands |> List.tryHead |> Option.exists (fun h -> h = hand)
        let label = if isActive then "▶ " else "  "
        let handLabel = $"{label}Hand {i + 1}"
        let betText = if state.Phase <> RoundComplete then $" (Bet: {hand.Bet})" else ""
        green () (fun () -> printfn $"── {handLabel}{betText} ──")
        let pCards = renderCards hand.Cards false false
        pCards |> List.iter (fun line -> printfn $"  {line}")
        let score = hand.Score
        let soft = if hand.IsSoft then " (soft)" else ""
        let status =
            if hand.IsBust then " BUST!"
            elif hand.IsBlackjack then " BLACKJACK!"
            elif hand.IsStood then " STAND"
            elif hand.IsDoubledDown then " DBL"
            else ""
        printfn $"  Score: {score}{soft}{status}"
    )

    // Actions available
    match state.Phase with 
    | PlayerTurn ->
        let active = state.PlayerHands |> List.tryHead
        match active with
        | Some hand when not hand.IsDone ->
            // Basic strategy recommendation
            if state.DealerHand.Cards.Length >= 2 then
                let upCard = state.DealerHand.Cards.[1]
                let rec_str = basicStrategy hand upCard
                printfn ""
                yellow () (fun () ->
                    printfn $"  Odds: vs dealer {upCard.Rank.HighValue}"
                    printfn $"  Player's card recommends: {rec_str}"
                )
            printfn ""
            white () (fun () ->
                printfn "  Actions: (H)it | (S)tand"
                if hand.Cards.Length = 2 && state.Player.Chips >= hand.Bet then
                    printfn "           (D)ouble down"
                if canSplit hand state then
                    printfn "           S(p)lit"
            )
        | _ -> ()
    | Betting ->
        printfn ""
        white () (fun () ->
            printf "  Enter bet amount: "
        )
    | InsuranceOffer ->
        printfn ""
        let bet = state.Player.CurrentBet |> Option.defaultValue 0
        white () (fun () ->
            printfn $"  Dealer shows Ace — buy insurance? ({bet / 2} chips)"
            printf  "  Insurance pays 2:1 if dealer has Blackjack. (Y/N): "
        )
    | RoundComplete ->
        printfn ""
        white () (fun () ->
            printfn "  Press Enter to continue..."
        )
    | _ -> ()

    printfn ""

/// Render the final outcome of a round
let renderOutcome (outcome: HandOutcome) (winnings: int) (playerChips: int) =
    printfn ""
    printfn "══════════════════════════════════════"
    match outcome with
    | Blackjack ->
        yellow () (fun () -> printfn $"  {outcome} +{winnings} chips!")
    | Win _ ->
        green () (fun () -> printfn $"  {outcome} +{winnings} chips!")
    | Lose _ ->
        red () (fun () -> printfn $"  {outcome}")
    | Push ->
        gray () (fun () -> printfn $"  {outcome} Bet returned.")
    printfn $"  Chips: {playerChips}"
    printfn "══════════════════════════════════════"

/// Game over screen
let renderGameOver (name: string) (chips: int) (rounds: int) =
    tryClear ()
    magenta () (fun () ->
        printfn "╔══════════════════════════════════════╗"
        printfn "║           GAME OVER                  ║"
        printfn "╚══════════════════════════════════════╝"
    )
    printfn ""
    printfn $"  Thanks for playing, {name}!"
    printfn $"  Final chips: {chips}"
    printfn $"  Rounds played: {rounds}"
    printfn ""

/// Welcome screen with name input
let renderWelcome () =
    tryClear ()
    magenta () (fun () ->
        printfn "╔══════════════════════════════════════╗"
        printfn "║     ♠♥ WELCOME TO BLACKJACK ♣♦       ║"
        printfn "╚══════════════════════════════════════╝"
    )
    printfn ""
    printf  "  Enter your name: "
    let name = Console.ReadLine()
    if String.IsNullOrWhiteSpace(name) then "Player" else name.Trim()
