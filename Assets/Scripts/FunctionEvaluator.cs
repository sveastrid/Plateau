using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

public class FunctionEvaluator : MonoBehaviour
{
    public static float EvaluateFunction(string function, float x, float y)
    {
        List<float> numbers = new List<float>();
        List<char> operations = new List<char>();

        bool endOfFunction = false;

        if (function.IndexOf(' ') != -1)
        {
            throw new InvalidOperationException("Don't put spaces in your function, please");
        }
        while (!endOfFunction)
        {
            if (function.Length == 0)
            {
                throw new InvalidOperationException("Possible missing number in your function");
            }
            if (function[0] == '(')
            {
                string insideParenthesis = FindInnerExpression(function);
                numbers.Add(EvaluateFunction(insideParenthesis, x, y));
                function = function.Substring(insideParenthesis.Length + 2);
            }
            else if (function[0] == 'c' || function[0] == 's' || function[0] == 't')
            {
                if (function[3] != '(')
                {
                    throw new InvalidOperationException("Trig function needs parenthesis around argument");
                }
                string insideParenthesis = FindInnerExpression(function.Substring(3));
                if (function[0] == 'c')
                {
                    numbers.Add((float)Mathf.Cos(EvaluateFunction(insideParenthesis, x, y)));
                    function = function.Substring(insideParenthesis.Length + 5);
                }
                else if (function[0] == 's')
                {
                    numbers.Add((float)Mathf.Sin(EvaluateFunction(insideParenthesis, x, y)));
                    function = function.Substring(insideParenthesis.Length + 5);
                }
                else if (function[0] == 't')
                {
                    numbers.Add((float)Mathf.Tan(EvaluateFunction(insideParenthesis, x, y)));
                    function = function.Substring(insideParenthesis.Length + 5);
                }
            }
            else if (function[0] == 'p')
            {
                numbers.Add(Mathf.PI);
                function = function.Substring(2);
            }
            else if (function[0] == 'e')
            {
                numbers.Add(Mathf.Exp(1));
                function = function.Substring(1);
            }
            else if (function[0] == 'x')
            {
                numbers.Add(x);
                function = function.Substring(1);
            }
            else if (function[0] == 'y')
            {
                numbers.Add(y);
                function = function.Substring(1);
            }
            else if (function[0] == '-')
            {
                if (operations.Count == 0)
                {
                    numbers.Add(0); //the '-' will still stay in the string because it will ultimately get added to the operations list
                }
                else
                {
                    throw new InvalidOperationException("You have two operations in a row");
                }
                
            }
            else if (!(function[0] == '-' || function[0] == '.' || char.IsDigit(function[0])))
            {
                throw new InvalidOperationException("Function couldn't find the first number.  Possibly two operation in a row.");
            }
            else
            {
                int j = 0;
                string num = "";
                int decimalCount = 0;
                while (char.IsDigit(function[j]) || function[j] == '.')
                {
                    if (function[j] == '.')
                    {
                        decimalCount++;
                    }
                    if (decimalCount > 1)
                    {
                        throw new InvalidOperationException("A number has too many decimals");
                    }
                    num += function[j];
                    j++;
                    if (j >= function.Length)
                    {
                        break;
                    }
                }
                try
                {
                    numbers.Add(float.Parse(num));
                }
                catch (FormatException)
                {
                    throw new InvalidOperationException("Error parsing a numerical value.  Check your function format.");
                }
                
                function = function.Substring(j);
            }

            if (function.Length == 0)
            {
                endOfFunction = true;
            }
            else if (function[0] == '-' || function[0] == '+' || function[0] == '*' || function[0] == '/' || function[0] == '^')
            {
                operations.Add(function[0]);
                function = function.Substring(1);
            }
            else
            {
                throw new InvalidOperationException("No operation after a number (make sure you are using * for multiplication)");
            }
        }

        if (function.Length > 0)
        {
            throw new InvalidOperationException("The function could not be interpreted as inputed.  For loop ran to completion.");
        }
        else
        {
            if (operations.Count == 0)
            {
                return numbers[0];
            }
            return EvaluateInOrder(numbers, operations);
        }
    }

    private static float EvaluateInOrder(List<float> numbers, List<char> operations)
    {
        int index = operations.IndexOf('^');
        while (index != -1)
        {
            operations.RemoveAt(index);
            numbers[index + 1] = (float)Mathf.Pow(numbers[index], numbers[index + 1]);
            numbers.RemoveAt(index);
            index = operations.IndexOf('^');
        }
        index = operations.IndexOf('*');
        while (index != -1)
        {
            operations.RemoveAt(index);
            numbers[index + 1] = numbers[index] * numbers[index + 1];
            numbers.RemoveAt(index);
            index = operations.IndexOf('*');
        }
        index = operations.IndexOf('/');
        while (index != -1)
        {
            operations.RemoveAt(index);
            numbers[index + 1] = numbers[index] / numbers[index + 1];
            numbers.RemoveAt(index);
            index = operations.IndexOf('/');
        }
        index = operations.IndexOf('-');
        while (index != -1)
        {
            operations.RemoveAt(index);
            numbers[index + 1] = numbers[index] - numbers[index + 1];
            numbers.RemoveAt(index);
            index = operations.IndexOf('-');
        }
        index = operations.IndexOf('+');
        while (index != -1)
        {
            operations.RemoveAt(index);
            numbers[index + 1] = numbers[index] + numbers[index + 1];
            numbers.RemoveAt(index);
            index = operations.IndexOf('+');
        }
        

        if (numbers.Count > 1)
        {
            throw new InvalidOperationException("Didn't recognize an operation");
        }
        return numbers[0];
    }

    private static string FindInnerExpression(string expression)
    {
        int unclosedCount = 1;
        int i = 1;
        while (unclosedCount > 0)
        {
            if (expression[i] == '(')
            {
                unclosedCount++;
            }
            if (expression[i] == ')')
            {
                unclosedCount--;
            }
            i++;
            if (i > expression.Length)
            {
                throw new InvalidOperationException("Unbalanced Parenthesis");
            }
        }
        return expression.Substring(1, i - 2);
    }
}