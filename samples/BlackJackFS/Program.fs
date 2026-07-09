module BlackJackFS.Program

open System
open System.Threading.Tasks
open BlackJackFS.Domain
open BlackJackFS.GameLogic
open BlackJackFS.Rendering

// ═══════════════════════════════════════════════════════════════
// Program.fs — the game loop
// Showcases: task { } CE, custom ResultBuilder CE, active patterns
// ═══════════════════════════════════════════════════════════════

// ── Custom ResultBuilder Computation Expression ────────────────
// Enables railway-oriented programming with `result { }` blocks
// Each `let!` line short-circuits on Error, continuing only on Ok

type ResultBuilder() =
    member _.Bind(x: Result<'T, 'E>, f: 'T -> Result<'U, 'E>) : Result<'U, 'E> =
        match x with
        | Ok value -> f value
        | Error e -> Error e

    member _.Return(x: 'T) : Result<'T, 'E> = Ok x

    member _.ReturnFrom(x: Result<'T, 'E>) : Result<'T, 'E> = x

    member _.Zero() : Result<unit, 'E> = Ok ()

    member _.Delay(f: unit -> Result<'T, 'E>) : unit -> Result<'T, 'E> = f

    member _.Run(f: unit -> Result<'T, 'E>) : Result<'T, 'E> = f ()

    member _.Combine(r: Result<unit, 'E>, f: unit -> Result<'T, 'E>) : Result<'T, 'E> =
        match r with
        | Ok () -> f ()
        | Error e -> Error e

let result = ResultBuilder()

// ── Active Patterns for input parsing ──────────────────────────
// Pattern-match on user input strings

/// Matches a valid integer
let (|Int|_|) (s: string) : int option =
    match Int32.TryParse(s.Trim()) with
    | true, n -> Some n
    | _ -> None

/// Matches player actions: (H)it, (S)tand, (D)ouble, S(p)lit
let (|Action|_|) (s: string) : PlayerAction option =
    match s.Trim().ToUpperInvariant() with
    | "H" | "HIT"     -> Some Hit
    | "S" | "STAND"   -> Some Stand
    | "D" | "DOUBLE"  -> Some DoubleDown
    | "P" | "SPLIT"   -> Some Split
    | _ -> None

/// Matches quit commands
let (|Quit|_|) (s: string) : unit option =
    match s.Trim().ToUpperInvariant() with
    | "Q" | "QUIT" | "EXIT" -> Some ()
    | _ -> None

// ── Input helpers ──────────────────────────────────────────────

/// Read a line from console, trimming whitespace
let readInputAsync () : Task<string> =
    task {
        let! line = Console.In.ReadLineAsync()
        return line.Trim()
    }

// ── Player turn loop ──────────────────────────────────────────

/// Check if every player hand (completed + active) has busted
let private allHandsBusted (state: GameState) : bool =
    let all = state.CompletedHands @ state.PlayerHands
    not (List.isEmpty all) && List.forall (fun (h: Hand) -> h.IsBust) all

/// Run the player's turn: prompt for actions until stand/bust/double/split
let rec playerTurnLoop (state: GameState) : Task<GameState> =
    task {
        if state.Phase <> PlayerTurn then
            return! Task.FromResult state
        elif state.PlayerHands.IsEmpty then
            let nextPhase = if allHandsBusted state then RoundComplete else DealerTurn
            return { state with Phase = nextPhase }
        else
            renderGame state true

            let activeHand = state.PlayerHands.Head
            let remainingHands = state.PlayerHands.Tail

            if activeHand.IsDone then
                // Move completed hand to completed list
                let s = { state with
                            PlayerHands = remainingHands
                            CompletedHands = activeHand :: state.CompletedHands }
                return! playerTurnLoop s
            else
                printf "  > "
                let! input = readInputAsync ()
                match input with
                | Quit ->
                    let s = { state with
                                PlayerHands = []
                                CompletedHands = state.PlayerHands @ state.CompletedHands
                                Phase = RoundComplete
                                Message = Some "Quit early." }
                    return! Task.FromResult s
                | Action action ->
                    let isSplit = match action with Split -> true | _ -> false
                    match applyPlayerAction action state with
                    | Ok newState ->
                        if isSplit then
                            let h1 = newState.PlayerHands.Head
                            let h2 = newState.PlayerHands.Tail |> List.tryHead
                            printfn "\n[SPLIT DEBUG] Hands=%d" newState.PlayerHands.Length
                            printfn "  H1: IsDone=%b IsStood=%b IsBust=%b Score=%d" h1.IsDone h1.IsStood h1.IsBust h1.Score
                            printfn "       Cards=%A" h1.Cards
                            match h2 with
                            | Some h -> printfn "  H2: IsDone=%b IsStood=%b IsBust=%b Score=%d" h.IsDone h.IsStood h.IsBust h.Score
                                        printfn "       Cards=%A" h.Cards
                            | None -> ()
                        let active = newState.PlayerHands.Head
                        let rest = newState.PlayerHands.Tail
                        if active.IsDone then
                            // Show the action result, then move hand to completed
                            renderGame newState true
                            let s = { newState with PlayerHands = rest; CompletedHands = active :: newState.CompletedHands }
                            if rest.IsEmpty then
                                let nextPhase = if allHandsBusted s then RoundComplete else DealerTurn
                                return { s with Phase = nextPhase }
                            else
                                return! playerTurnLoop s
                        else
                            return! playerTurnLoop newState
                    | Error msg ->
                        let s = { state with Message = Some msg }
                        return! Task.FromResult s
                | _ ->
                    let s = { state with Message = Some "Invalid input. Use H, S, D, or P." }
                    return! Task.FromResult s
    }

// ── Betting phase ─────────────────────────────────────────────

let rec bettingLoop (state: GameState) : Task<GameState> =
    task {
        renderGame state false

        let defaultBet = 100
        printf "100"

        let sb = System.Text.StringBuilder()
        let mutable cleared = false

        let rec readKeys () : Task<GameState> =
            task {
                let ki = Console.ReadKey(true)
                match ki.Key with
                | ConsoleKey.Q ->
                    printfn ""
                    return { state with Phase = RoundComplete; Message = Some "Quit early." }
                | ConsoleKey.Enter ->
                    printfn ""
                    let input =
                        if sb.Length = 0 && not cleared then string defaultBet
                        else sb.ToString()
                    match Int32.TryParse(input) with
                    | true, amount ->
                        match placeBet amount state with
                        | Ok afterBet ->
                            let finalState = afterBet |> afterDeal
                            return finalState
                        | Error msg ->
                            return! bettingLoop { state with Message = Some msg }
                    | _ ->
                        return! bettingLoop { state with Message = Some "Enter a valid number." }
                | ConsoleKey.Backspace ->
                    if sb.Length > 0 then
                        sb.Remove(sb.Length - 1, 1) |> ignore
                        Console.Write("\b \b")
                    return! readKeys ()
                | _ ->
                    if System.Char.IsDigit(ki.KeyChar) then
                        if not cleared then
                            for _ in string defaultBet do
                                Console.Write("\b \b")
                            cleared <- true
                        sb.Append(ki.KeyChar) |> ignore
                        Console.Write(ki.KeyChar)
                    return! readKeys ()
            }

        return! readKeys ()
    }

// ── Round resolution ──────────────────────────────────────────

let resolveRound (state: GameState) : GameState * int =
    let allHands = state.CompletedHands @ state.PlayerHands
    let totalWinnings =
        allHands
        |> List.sumBy (fun h ->
            let outcome = resolveHand h state.DealerHand
            payout h outcome)
    // Insurance payout: 2:1 on half bet = full bet returned
    let insurancePayout =
        if state.InsuranceTaken && state.DealerHand.IsBlackjack then
            state.Player.CurrentBet |> Option.defaultValue 0
        else 0
    let totalBet = allHands |> List.sumBy (fun h -> h.Bet)
    let newChips = state.Player.Chips + totalWinnings + insurancePayout
    let net = totalWinnings + insurancePayout - totalBet
    { state with
        Player = { state.Player with Chips = newChips }
        Message = Some $"Round complete. Net: {net} chips" },
    totalWinnings + insurancePayout

// ── Insurance phase ───────────────────────────────────────────

let rec insuranceLoop (state: GameState) : Task<GameState> =
    task {
        renderGame state true

        if not (canBuyInsurance state) then
            return! state |> checkDealerBlackjack |> Task.FromResult
        else
            let! input = readInputAsync ()
            match input.Trim().ToUpperInvariant() with
            | "Y" | "YES" ->
                return! state |> buyInsurance |> checkDealerBlackjack |> Task.FromResult
            | _ ->
                return! state |> checkDealerBlackjack |> Task.FromResult
    }

// ── Main game loop ────────────────────────────────────────────

let rec gameLoop (state: GameState) : Task<GameState> =
    task {
        match state.Phase with
        | Betting ->
            let! state' = bettingLoop state
            if state'.Message = Some "Quit early." then
                return! Task.FromResult state'
            else
                return! gameLoop state'

        | Dealing ->
            let state' = afterDeal state
            return! gameLoop state'

        | InsuranceOffer ->
            let! state' = insuranceLoop state
            return! gameLoop state'

        | PlayerTurn ->
            let! state' = playerTurnLoop state
            return! gameLoop state'

        | DealerTurn ->
            let state' = executeDealerTurn state
            return! gameLoop state'

        | RoundComplete ->
            let resolved, totalWinnings = resolveRound state
            renderGame resolved false
            // Show per-hand results
            (resolved.CompletedHands @ resolved.PlayerHands) |> List.iteri (fun i hand ->
                let outcome = resolveHand hand resolved.DealerHand
                let won = payout hand outcome
                renderOutcome outcome won resolved.Player.Chips
            )

            if resolved.Player.Chips <= 0 then
                renderGameOver resolved.Player.Name resolved.Player.Chips resolved.RoundNumber
                printf "  Try again? (y/n): "
                let! input = readInputAsync ()
                match input.Trim().ToUpperInvariant() with
                | "Y" | "YES" ->
                    let fresh = initGame resolved.Player.Name 1000 resolved.DeckCount
                    return! gameLoop fresh
                | _ ->
                    return! Task.FromResult resolved
            else
                let! _ = Console.In.ReadLineAsync()
                return! resolved |> newRound |> gameLoop
    }

// ── Entry point ───────────────────────────────────────────────

[<EntryPoint>]
let main argv =
    task {
        let name = renderWelcome ()

        // ── Use the custom ResultBuilder for validation ────────
        let chipsResult = result {
            let! n = Ok 1000  // starting chips
            return n
        }

        match chipsResult with
        | Ok chips ->
            let deckCount = 2  // default 2 decks; change to 1-4
            let initialState = initGame name chips deckCount
            let! finalState = gameLoop initialState
            printfn $"  Game ended. Final state: Phase={finalState.Phase}, Chips={finalState.Player.Chips}"
        | Error e ->
            printfn $"Error: {e}"

        return 0
    }
    |> fun t -> t.Result
