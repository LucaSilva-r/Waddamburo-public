#if (defined(WADDAMBURO_BUILD_DEBUG) + defined(WADDAMBURO_BUILD_RELEASE) + \
     defined(WADDAMBURO_BUILD_SANITIZE)) != 1
#error "Exactly one supported native build configuration must be selected"
#endif

int main(void)
{
    return 0;
}
