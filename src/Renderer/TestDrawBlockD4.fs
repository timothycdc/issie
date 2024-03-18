module TestDrawBlockD4
open Elmish

open TestDrawBlockD1.Tests
open TestDrawBlock
open TestLib
open TestDrawBlock.HLPTick3
open TestDrawBlock.HLPTick3.Asserts
open TestDrawBlock.HLPTick3.Builder
open TestDrawBlock.HLPTick3.Tests

open EEExtensions
open Optics
open Optics.Operators
open DrawHelpers
open Helpers
open CommonTypes
open ModelType
open DrawModelType
open Sheet.SheetInterface
open GenerateData
open SheetBeautifyHelpers


module Tests =

    /// List of tests available which can be run ftom Issie File Menu.
    /// The first 9 tests can also be run via Ctrl-n accelerator keys as shown on menu
    let testsToRunFromSheetMenu : (string * (int -> int -> Dispatch<Msg> -> Unit)) list =
        // Change names and test functions as required
        // delete unused tests from list
        [
            "Test1", fun _ _ _ -> printf "Test1" // RANDOM TEST
            "Test2", fun _ _ _ -> printf "Test2"
            "Test3", fun _ _ _ -> printf "Test3"
            "Test4", fun _ _ _ -> printf "Test4"
            "Test5", fun _ _ _ -> printf "Test5"
            "Test6", fun _ _ _ -> printf "Test6"
            "Test7", fun _ _ _ -> printf "Test5"
            "Test8", fun _ _ _ -> printf "Test5"
            "Next Test Error", fun _ _ _ -> printf "Next Error:" // Go to the nexterror in a test
        ]

    /// Display the next error in a previously started test
    let nextError (testName, testFunc) firstSampleToTest dispatch =
        let testNum =
            testsToRunFromSheetMenu
            |> List.tryFindIndex (fun (name,_) -> name = testName)
            |> Option.defaultValue 0
        testFunc testNum firstSampleToTest dispatch

    /// common function to execute any test.
    /// testIndex: index of test in testsToRunFromSheetMenu
    let testMenuFunc (testIndex: int) (dispatch: Dispatch<Msg>) (model: Model) =
        let name,func = testsToRunFromSheetMenu[testIndex] 
        printf "%s" name
        match name, model.DrawBlockTestState with
        | "Next Test Error", Some state ->
            nextError testsToRunFromSheetMenu[state.LastTestNumber] (state.LastTestSampleIndex+1) dispatch
        | "Next Test Error", None ->
            printf "Test Finished"
            ()
        | _ ->
            func testIndex 0 dispatch
    


    

