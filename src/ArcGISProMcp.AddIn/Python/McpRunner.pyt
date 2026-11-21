# -*- coding: utf-8 -*-
"""ArcGIS Pro MCP runner.

Executes Python code sent by an AI agent *inside* the running ArcGIS Pro process,
so arcpy.mp.ArcGISProject("CURRENT") and every arcpy module work against the
open project. Variables persist between calls (like a notebook session).
"""
import arcpy
import ast
import base64
import builtins
import contextlib
import io
import json
import traceback

_STATE_KEY = "_arcgispro_mcp_globals"


def _session(reset=False):
    g = None if reset else getattr(builtins, _STATE_KEY, None)
    if g is None:
        g = {"__name__": "__mcp__", "__builtins__": builtins, "arcpy": arcpy}
        try:
            g["aprx"] = arcpy.mp.ArcGISProject("CURRENT")
        except Exception:
            pass
        setattr(builtins, _STATE_KEY, g)
    else:
        try:
            g["aprx"] = arcpy.mp.ArcGISProject("CURRENT")
        except Exception:
            pass
    return g


def _jsonable(v, depth=0):
    if depth > 6:
        return repr(v)
    if v is None or isinstance(v, (bool, int, float, str)):
        return v
    if isinstance(v, dict):
        return {str(k): _jsonable(x, depth + 1) for k, x in list(v.items())[:5000]}
    if isinstance(v, (list, tuple, set)):
        return [_jsonable(x, depth + 1) for x in list(v)[:5000]]
    try:
        return json.loads(json.dumps(v))
    except Exception:
        return repr(v)


class Toolbox(object):
    def __init__(self):
        self.label = "ArcGIS Pro MCP Runner"
        self.alias = "mcprunner"
        self.tools = [RunCode]


class RunCode(object):
    def __init__(self):
        self.label = "Run Code"
        self.description = "Runs base64-encoded Python code inside ArcGIS Pro for the MCP bridge."
        self.canRunInBackground = False

    def getParameterInfo(self):
        code = arcpy.Parameter(displayName="Code (base64)", name="code_b64", datatype="GPString",
                               parameterType="Required", direction="Input")
        out = arcpy.Parameter(displayName="Output JSON file", name="out_file", datatype="GPString",
                              parameterType="Required", direction="Input")
        reset = arcpy.Parameter(displayName="Reset session", name="reset", datatype="GPString",
                                parameterType="Optional", direction="Input")
        return [code, out, reset]

    def isLicensed(self):
        return True

    def execute(self, parameters, messages):
        src = base64.b64decode(parameters[0].valueAsText).decode("utf-8")
        out_file = parameters[1].valueAsText
        reset = (parameters[2].valueAsText or "false").lower() == "true"
        g = _session(reset)
        g["result"] = None
        out, err = io.StringIO(), io.StringIO()
        payload = {"ok": True}
        try:
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                tree = ast.parse(src, "<mcp>", "exec")
                last_expr = None
                if tree.body and isinstance(tree.body[-1], ast.Expr):
                    last_expr = ast.Expression(tree.body.pop().value)
                exec(compile(tree, "<mcp>", "exec"), g)
                if last_expr is not None:
                    value = eval(compile(last_expr, "<mcp>", "eval"), g)
                    if value is not None and g.get("result") is None:
                        g["result"] = value
        except BaseException:
            payload["ok"] = False
            payload["error"] = traceback.format_exc()[-20000:]
        payload["stdout"] = out.getvalue()[-200000:]
        e = err.getvalue()
        if e:
            payload["stderr"] = e[-50000:]
        payload["result"] = _jsonable(g.get("result"))
        with io.open(out_file, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, default=str, ensure_ascii=False)
