# Steps: Game Rules

## 🎯 Objective
The primary goal is to be the first player to capture **12 of your opponent's tiles**. 

> **Note:** The rules below describe the original board game. In this digital implementation, **there is no rigid turn order**. Either player may play at any time, including during the opponent's turn.

## 🎲 Setup
Players place their pawns on an empty space on the board. (Either player may place their pawn first.)

## 🚶 Gameplay & Movement
During your move, you may move your pawn across the board. 
* **Valid Direction:** You can move to any of the 8 adjacent spaces.
* **Elevation Limits:** The space you move to must be exactly **one level higher** or **one level lower** than your current space.
* **Obstacles:** You cannot move onto a space occupied by the opponent's pawn or the opponent's tiles (unless you are making a capture).
* **Multi-Step Moves:** You may move multiple spaces in a single turn. 
* **Momentum Rule:** You cannot change vertical direction during a normal movement phase. If you start your turn by moving UP to a higher level, all subsequent moves that turn must also be UP. If you start moving DOWN, all moves must be DOWN. *(Exception: See Capturing).*

*On your turn, you must either Capture or Build.*

## ⚔️ Action 1: Capturing Tiles
* **How to Capture:** You capture your opponent's tiles by moving your pawn from an adjacent space that is at least one level higher **down** onto their tiles.
* **Collecting:** Take the captured tiles off the board and place them in front of you to keep score.
* **Turn Ends:** Your turn ends immediately after completing a capture.

## 🧱 Action 2: Building (If You Don't Capture)
If you do not capture any of your opponent's tiles during your turn, you must build:
* **How Many to Build:** The number of tiles you place is equal to the number of spaces you moved during that turn.
* **Minimum Rule:** You must always place **at least 1 tile** at the end of your turn, even if you moved zero spaces or were unable to move.
* **Where to Build:** 
  * You can place tiles on empty squares on the board.
  * You can stack tiles on squares where you have previously placed your own tiles.
  * You **may** build directly under your own pawn.
  * You **may not** build under your opponent's pawn.

## 🏆 Winning the Game
* **Standard Win:** The first player to successfully capture **12** of the opponent's tiles wins immediately.
* **Supply Exhaustion Win:** If the central supply of tiles runs out, the game ends. The player who has captured the most opponent tiles wins.
